using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Interop;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

internal enum UuViewTarget { Host, Child }

/// <summary>
/// 串行执行 UU 手动切换、视角纠正与服务修复。结束串流端只触发一次重新选会话；
/// 超时直接报告失败，不再启动独立的恢复循环，也不自动把分身连回后台。
/// UU 的会话选择属于外部行为，只有观测到目标串流端才报告落点已确认。
/// </summary>
internal sealed class UuViewSwitchService : IDisposable
{
    private const string StreamerProcessName = "GameViewerServer";
    private const string StreamerServiceName = "GameViewerService";
    private static readonly TimeSpan DisconnectWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StreamerSettleWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ServiceCommandTimeout = TimeSpan.FromSeconds(10);
    private readonly ChildSessionService _childSessionService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _disposed;

    /// <summary>创建切换服务。</summary>
    public UuViewSwitchService(ChildSessionService childSessionService) => _childSessionService = childSessionService;

    /// <summary>其他操作可以在空闲边界重新检查目标；回调可能来自线程池。</summary>
    internal event EventHandler? BecameIdle;

    /// <summary>Deferred 表示尚未执行，等待操作空闲后再评估。</summary>
    public readonly record struct Result(bool Success, string Message, UuViewTarget? Target = null, bool Deferred = false);

    /// <summary>用户主动切换，允许创建分身。</summary>
    public Task<Result> ToggleAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(token => SwitchAsync(null, token), cancellationToken);

    internal Task<Result> EnsureTargetAsync(UuViewTarget target, CancellationToken cancellationToken) =>
        ExecuteAsync(token => SwitchAsync(target, token), cancellationToken, automatic: true);

    internal static UuViewTarget CaptureTarget()
    {
        var streamers = GetStreamerSessionIds();
        var child = ChildSessionNativeMethods.TryGetChildSessionId();
        return child is { } id && streamers.Contains(id) ? UuViewTarget.Child : UuViewTarget.Host;
    }

    internal static bool NeedsCorrection(UuViewTarget target)
    {
        var session = target == UuViewTarget.Host
            ? ChildSessionNativeMethods.TryGetConsoleSessionId()
            : ChildSessionNativeMethods.TryGetChildSessionId();
        var streamers = GetStreamerSessionIds();
        return session is { } id && streamers.Count > 0 && !streamers.Contains(id);
    }

    private async Task<Result> ExecuteAsync(Func<CancellationToken, Task<Result>> operation,
        CancellationToken cancellationToken, bool automatic = false)
    {
        ThrowIfDisposed();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _childSessionService.SessionCancellationToken, _shutdown.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(90));
        var acquired = false;
        try
        {
            if (automatic) acquired = await _gate.WaitAsync(0, lifetime.Token).ConfigureAwait(false);
            else
            {
                await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                acquired = true;
            }
            if (!acquired) return new Result(false, Locale.Get("Uu.Result.Busy"), Deferred: true);
            lifetime.Token.ThrowIfCancellationRequested();
            return await Task.Run(() => operation(lifetime.Token), lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, Locale.Get("Uu.Result.Cancelled"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
        {
            AppLog.Warn("UU 操作失败。", exception);
            return new Result(false, Locale.Format("Uu.Result.Failed", exception.GetBaseException().Message));
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
                // 自动纠正不订阅自己的“完成”事件；真实的会话/进程事件仍由锁定层合并。
                if (!_disposed && !automatic) BecameIdle?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task<Result> SwitchAsync(UuViewTarget? requested, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var streamers = GetStreamerSessionIds();
        var console = ChildSessionNativeMethods.TryGetConsoleSessionId();
        var child = ChildSessionNativeMethods.TryGetChildSessionId();
        var target = requested ?? ((console is { } host && streamers.Contains(host))
            || (streamers.Count == 0 && child is { } id && ChildSessionNativeMethods.IsSessionActive(id))
                ? UuViewTarget.Child : UuViewTarget.Host);
        if (requested.HasValue && !NeedsCorrection(target)) return new Result(true, string.Empty, target);
        AppLog.Info($"UU 视角切换：目标={target} 自动={requested.HasValue} 串流端=[{string.Join(',', streamers)}]");
        var result = target == UuViewTarget.Host
            ? await SwitchToMainAsync(token).ConfigureAwait(false)
            : await SwitchToChildAsync(token, requested.HasValue).ConfigureAwait(false);
        return result with { Target = result.Success ? target : null };
    }

    private async Task<Result> SwitchToMainAsync(CancellationToken token)
    {
        var child = ChildSessionNativeMethods.TryGetChildSessionId();
        await _childSessionService.DisconnectDesktopForSwitchAsync(token).ConfigureAwait(false);
        if (child is { } id && ChildSessionNativeMethods.IsSessionActive(id))
        {
            // 不先白等一轮：控件的断开和 WTS 请求针对同一个会话，统一观察一次。
            token.ThrowIfCancellationRequested();
            if (ChildSessionNativeMethods.TryGetChildSessionId() != id)
                return new Result(false, Locale.Get("Uu.Result.NoSession"));
            _ = ChildSessionNativeMethods.DisconnectSession(id);
            if (!await WaitUntilAsync(() => !ChildSessionNativeMethods.IsSessionActive(id), DisconnectWait, token).ConfigureAwait(false))
                return new Result(false, Locale.Get("Uu.Result.DisconnectFailed"));
        }
        token.ThrowIfCancellationRequested();
        var killed = KillStreamers(out var failed);
        if (failed) return new Result(false, Locale.Get("Uu.Result.StreamerKillFailed.NotRestored"));
        if (killed == 0) return new Result(true, Locale.Get("Uu.Result.ToMain.NoStreamer"));
        var confirmed = await WaitUntilAsync(() =>
            ChildSessionNativeMethods.TryGetConsoleSessionId() is { } host && GetStreamerSessionIds().Contains(host),
            StreamerSettleWait, token).ConfigureAwait(false);
        return new Result(confirmed, Locale.Get(confirmed ? "Uu.Result.ToMain" : "Uu.Result.ToMain.StreamerStuck"));
    }

    private async Task<Result> SwitchToChildAsync(CancellationToken token, bool existingSessionOnly)
    {
        var generation = _childSessionService.StartGeneration;
        if (existingSessionOnly && ChildSessionNativeMethods.TryGetChildSessionId() is null)
            return new Result(false, Locale.Get("Uu.Result.NoSession"));
        // 只有一条连接路径；注销 token 贯穿排队、启动和目标确认。
        var connected = await _childSessionService.EnsureDesktopConnectedAsync(token,
            existingSessionGeneration: existingSessionOnly ? generation : null).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (generation != _childSessionService.StartGeneration)
            return new Result(false, Locale.Get("Uu.Result.NoSession"));
        if (!connected) return new Result(false, Locale.Get("Uu.Result.NotConnected"));
        if (ChildSessionNativeMethods.TryGetChildSessionId() is not { } id)
            return new Result(false, Locale.Get("Uu.Result.NoSession"));
        var killed = KillStreamers(out var failed);
        if (failed) return new Result(false, Locale.Get("Uu.Result.StreamerKillFailed"));
        if (killed == 0) return new Result(true, Locale.Get("Uu.Result.ToChild.NoStreamer"));
        var confirmed = await WaitUntilAsync(() => GetStreamerSessionIds().Contains(id), StreamerSettleWait, token).ConfigureAwait(false);
        return new Result(confirmed, Locale.Get(confirmed ? "Uu.Result.ToChild" : "Uu.Result.ToChild.StreamerStuck"));
    }

    /// <summary>用户主动请求重启服务；不安排后续无限恢复。</summary>
    public Task<Result> RestartStreamerServiceAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        token.ThrowIfCancellationRequested();
        bool stopped;
        using (var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try { stopped = await RunServiceCommandAsync("stop", stopBudget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { stopped = false; }
        }
        // 停止一经下发，就用独立且有界的预算尽力启动回来，不能因注销而遗留停服。
        bool started;
        using (var startBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                if (!stopped) stopped = await WaitForServiceStateAsync(false, startBudget.Token).ConfigureAwait(false);
                started = await RunServiceCommandAsync("start", startBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { started = false; }
        }
        token.ThrowIfCancellationRequested();
        if (!stopped || !started)
            return new Result(false, Locale.Get(!started ? "Uu.Result.ServiceStartFailed" : "Uu.Result.ServiceStopFailed"));
        var confirmed = await WaitUntilAsync(() => GetStreamerSessionIds().Count > 0,
            TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        return new Result(true, Locale.Get(confirmed ? "Uu.Result.ServiceRestarted" : "Uu.Result.ServiceRestarted.NoStreamer"));
    }, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        // 在途操作仍须释放 gate，不在 Dispose 里销毁它。
    }

    private static async Task<bool> RunServiceCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "sc.exe"),
                $"\"{command}\" \"{StreamerServiceName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // 必须在等待进程前排空两个管道，避免子进程被输出缓冲区卡住。
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            commandTimeout.CancelAfter(ServiceCommandTimeout);

            try
            {
                await process.WaitForExitAsync(commandTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // sc.exe 自己卡住（服务控制管理器不响应）：杀掉它按失败处理，
                // 否则调用方会在这里无限等下去，恢复循环也永远退不出去。
                KillQuietly(process);
                AppLog.Warn($"sc {command} {StreamerServiceName} 超过 {ServiceCommandTimeout.TotalSeconds} 秒未返回。");
                return false;
            }
            catch (OperationCanceledException)
            {
                // 调用方取消：同样要把这个进程收掉。否则它会继续占着服务控制，
                // 和后续的重启操作撞在一起。
                KillQuietly(process);
                throw;
            }

            // sc.exe 的退出码不可全信：「服务已在运行」「服务已停止」都落在失败位。
            // 所以不看退出码，等状态收敛到目标值——否则 start 被拒也会报成功，
            // 上层「重启 UU 服务」就变成了一句空话。
            await Task.WhenAll(output, error).ConfigureAwait(false);
            AppLog.Info($"sc {command} {StreamerServiceName} 退出码 {process.ExitCode}。");

            var expectRunning = !string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase);
            return await WaitForServiceStateAsync(expectRunning, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or IOException)
        {
            AppLog.Warn($"执行 sc {command} 失败。", exception);
            return false;
        }
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
    }

    private static Task<bool> WaitForServiceStateAsync(bool expectRunning, CancellationToken token) =>
        WaitUntilAsync(() => ServiceNativeMethods.TryQueryState(StreamerServiceName, out var state)
            && state == (expectRunning ? 4 : 1), TimeSpan.FromSeconds(15), token);

    /// <summary>查询当前串流端会话；无法读取的进程不参与判断。</summary>
    internal static List<uint> GetStreamerSessionIds()
    {
        var sessions = new List<uint>();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(StreamerProcessName);
        }
        catch (InvalidOperationException)
        {
            return sessions;
        }

        foreach (var process in processes)
        {
            try
            {
                sessions.Add((uint)process.SessionId);
            }
            catch (Exception exception) when (exception is Win32Exception
                                                  or InvalidOperationException
                                                  or NotSupportedException)
            {
                // 进程刚好退出，或权限不足读不到会话号：跳过即可。
            }
            finally
            {
                process.Dispose();
            }
        }

        return sessions;
    }

    /// <summary>
    /// 结束全部 UU 串流端，返回结束掉的个数。
    ///
    /// UU 的服务会自动把它重新拉起来，重拉时就会重新挑会话——这正是切换的杠杆。
    /// </summary>
    /// <param name="failed">有进程存在但一个都没杀掉时为 true。</param>
    private static int KillStreamers(out bool failed)
    {
        failed = false;
        _ = ProcessNativeMethods.TryEnableDebugPrivilege();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(StreamerProcessName);
        }
        catch (InvalidOperationException)
        {
            failed = true;
            return 0;
        }

        var killed = 0;
        foreach (var process in processes)
        {
            var processId = process.Id;
            try
            {
                if (ProcessNativeMethods.TryTerminateProcess(processId, out var errorCode))
                {
                    killed++;
                }
                else
                {
                    // 原生调用失败时退回托管实现，覆盖「句柄拿不到但进程能杀」的情况。
                    process.Kill();
                    killed++;
                    AppLog.Warn($"结束串流端 {processId} 走托管路径（Win32 错误 {errorCode}）。");
                }
            }
            catch (Exception exception) when (exception is Win32Exception
                                                  or InvalidOperationException
                                                  or NotSupportedException)
            {
                failed = true;
                AppLog.Warn($"无法结束串流端进程 {processId}。", exception);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (killed < processes.Length)
        {
            failed = true;
        }

        if (killed > 0)
        {
            AppLog.Info($"已结束 {killed} 个 UU 串流端进程，等待服务重新挑选会话。");
        }

        return killed;
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                                  or Win32Exception)
            {
                // 查询期间会话状态在变，忽略单次失败，下一轮继续。
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UuViewSwitchService));
        }
    }
}
