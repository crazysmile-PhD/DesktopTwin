using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Input;
using DesktopTwin.Launching;

namespace DesktopTwin.Services;

/// <summary>One authenticated companion in one child session; all writes are serialized off the UI thread.</summary>
internal sealed class RelativeInputSession : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly InputPacketQueue _queue = new();
    private readonly InputWakeSignal _signal = new();
    private readonly RelativeInputTransport _transport;
    private Task _loop = Task.CompletedTask;
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private long _armedUntil;
    private int _statusRequested;
    private int _disposed;
    private readonly object _statusGate = new();
    private RelativeInputStatus? _status;
    internal RelativeInputStatus? Status { get { lock (_statusGate) return _status; } }
    private int _ready;
    internal bool Ready => Volatile.Read(ref _ready) != 0;
    /// <summary>Completes after the single exchange loop stops using its pipe and signal.</summary>
    internal Task Completion => _loop;
    internal string? Failure { get; private set; }
    internal string PipeName { get; } = "DesktopTwin.Input." + Guid.NewGuid().ToString("N");

    internal RelativeInputSession()
    {
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        _transport = new RelativeInputTransport(_pipe, _stop.Token);
    }

    internal static async Task<RelativeInputSession> ConnectAsync(uint sessionId, CancellationToken cancellationToken,
        Func<string, string, CancellationToken, Task>? launch = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = new RelativeInputSession();
            try
            {
                await session.StartAsync(sessionId, cancellationToken, launch).ConfigureAwait(false);
                return session;
            }
            catch (SessionLaunchReceiptMismatchException error) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                session.Dispose();
                AppLog.Warn("内部输入接收端创建核验失败，关闭旧通道后仅重试一次。", error);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch { session.Dispose(); throw; }
        }
    }

    internal async Task StartAsync(uint sessionId, CancellationToken cancellationToken,
        Func<string, string, CancellationToken, Task>? launch = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Input receiver already started.");
        try { await StartCoreAsync(sessionId, launch, cancellationToken).ConfigureAwait(false); }
        finally { _startup.TrySetResult(); }
    }

    private async Task StartCoreAsync(uint sessionId, Func<string, string, CancellationToken, Task>? launch,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var path = await Task.Run(() => SessionLaunchChannel.EnsureEmbeddedProgram("DesktopTwin.InputReceiver.exe"),
            cancellationToken).ConfigureAwait(false);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var arguments = $"--relative-input {PipeName} {Environment.ProcessId} {sessionId}";
        var launchTask = launch is null
            ? ChildSessionProcessLauncher.LaunchInputReceiverAsync(sessionId, path, arguments, budget.Token)
            : launch(path, arguments, budget.Token);
        var handshake = AcceptPeerAsync(path, sessionId, budget.Token);
        try
        {
            // A failed launch cannot produce a handshake. Preserve its cause without waiting for the pipe deadline.
            await Task.WhenAny(launchTask, handshake).ConfigureAwait(false);
            if (launchTask.IsCompleted) await launchTask.ConfigureAwait(false);
            await handshake.ConfigureAwait(false);
            // No foreground lease is issued by the controller until creation verification finishes.
            _loop = RunAsync(sessionId);
            await launchTask.ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            if (_loop.IsCompleted) throw new IOException(Failure ?? "Input receiver disconnected during startup.");
            Volatile.Write(ref _ready, 1);
            AppLog.Info($"会话内相对输入通道就绪：session={sessionId}。");
        }
        catch
        {
            Dispose();
            try { await handshake.ConfigureAwait(false); }
            catch (Exception error) { AppLog.Warn("输入接收端握手未完成。", error); }
            try { await launchTask.ConfigureAwait(false); }
            catch (Exception error) { AppLog.Warn("输入接收端启动未完成。", error); }
            throw;
        }
    }

    private async Task AcceptPeerAsync(string path, uint sessionId, CancellationToken cancellationToken)
    {
        await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        var hello = new byte[12];
        await _pipe.ReadExactlyAsync(hello, cancellationToken).ConfigureAwait(false);
        ValidatePeer(path, sessionId);
        if (RelativeInputProtocol.ReadInt32(hello, 0) != RelativeInputProtocol.Version
            || RelativeInputProtocol.ReadInt32(hello, 4) != sessionId
            || unchecked((uint)RelativeInputProtocol.ReadInt32(hello, 8)) != PeerProcessId())
            throw new IOException("Input receiver handshake mismatch.");
    }

    private uint PeerProcessId()
    {
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            throw new IOException("Cannot identify input receiver.");
        return pid;
    }
    private void ValidatePeer(string path, uint sessionId)
    {
        using var process = Process.GetProcessById(checked((int)PeerProcessId()));
        if (process.SessionId != sessionId || process.HasExited
            || !string.Equals(ProcessCreationIdentity.ReadImagePath(process.Id), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Input receiver process/session does not match.");
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? peer = null;
        _pipe.RunAsClient(() => { using var client = WindowsIdentity.GetCurrent(); peer = client.User; });
        if (identity.User is null || identity.User != peer) throw new IOException("Input receiver user does not match.");
    }

    // If the UI stalls or loses focus, its short authorization expires even while IO still runs.
    internal void SetForegroundLease(bool allowed)
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Exchange(ref _armedUntil, allowed ? now + 250 : 0);
        if (!allowed && previous != 0) _queue.Clear();
        // Repeated UI samples renew a lease, not another reset/heartbeat round trip.
        if (allowed ? previous <= now : previous != 0) Signal();
    }

    /// <summary>Requests one fresh status without granting a lease or starting a periodic heartbeat.</summary>
    internal void RequestStatus()
    {
        if (!Ready || Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _statusRequested, 1) == 0) Signal();
    }
    internal bool TrySend(InputPacket packet)
    {
        if (!Ready || Volatile.Read(ref _disposed) != 0) return false;
        if (!_queue.TryAdd(packet, out var requiresWake)) { Failure = "Input queue overflow; receiver disconnected."; Dispose(); return false; }
        if (requiresWake) Signal();
        return true;
    }

    private void Signal() => _signal.Set();
    private bool TakeStatusRequest() => Volatile.Read(ref _statusRequested) != 0
        && Interlocked.Exchange(ref _statusRequested, 0) != 0;

    private async Task RunAsync(uint sessionId)
    {
        try
        {
            var pump = new RelativeInputPump(_transport, _queue, _signal,
                () => Interlocked.Read(ref _armedUntil), TakeStatusRequest, PublishStatus);
            await pump.RunAsync(sessionId, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or Win32Exception)
        {
            if (!_stop.IsCancellationRequested) { Failure = error.Message; AppLog.Warn("相对输入通道已断开。", error); }
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
            // Release the adapter's borrowed handle before closing the pipe, including loop failures
            // that precede an explicit session disposal. Otherwise the peer cannot observe EOF.
            try { _transport.Dispose(); }
            finally { _pipe.Dispose(); }
        }
    }
    private void PublishStatus(RelativeInputStatus status)
    {
        // Copy one immutable value under a short lock; readers cannot see torn fields or buffer reuse.
        lock (_statusGate) _status = status with { Sequence = (_status?.Sequence ?? 0) + 1 };
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _ready, 0);
        _stop.Cancel();
        Signal();
        _pipe.Dispose();
        if (Volatile.Read(ref _started) == 0) _startup.TrySetResult();
        _ = _startup.Task.ContinueWith(async _ =>
        {
            await _loop.ConfigureAwait(false);
            _transport.Dispose();
            _signal.Dispose();
            _stop.Dispose();
        }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
}
