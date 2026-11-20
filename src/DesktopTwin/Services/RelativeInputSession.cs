using System;
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
    private readonly SemaphoreSlim _signal = new(0, 1);
    private Task _loop = Task.CompletedTask;
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private long _armedUntil;
    private int _disposed;
    private RelativeInputStatus? _status;
    internal RelativeInputStatus? Status => Volatile.Read(ref _status);
    private int _ready;
    internal bool Ready => Volatile.Read(ref _ready) != 0;
    internal string? Failure { get; private set; }
    internal string PipeName { get; } = "DesktopTwin.Input." + Guid.NewGuid().ToString("N");

    internal RelativeInputSession()
    {
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
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
        var path = SessionLaunchChannel.EnsureEmbeddedProgram("DesktopTwin.InputReceiver.exe");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var arguments = $"--relative-input {PipeName} {Environment.ProcessId} {sessionId}";
        var launchTask = launch is null
            ? ChildSessionProcessLauncher.LaunchInputReceiverAsync(sessionId, path, arguments, budget.Token)
            : launch(path, arguments, budget.Token);
        try
        {
            await _pipe.WaitForConnectionAsync(budget.Token).ConfigureAwait(false);
            var hello = new byte[12];
            await _pipe.ReadExactlyAsync(hello, budget.Token).ConfigureAwait(false);
            ValidatePeer(path, sessionId);
            using var reader = new BinaryReader(new MemoryStream(hello));
            if (reader.ReadInt32() != RelativeInputProtocol.Version || reader.ReadInt32() != sessionId
                || reader.ReadUInt32() != PeerProcessId()) throw new IOException("Input receiver handshake mismatch.");
            // Keep an unarmed receiver alive while task/creation verification finishes.
            // No foreground lease is issued by the controller until Ready becomes true.
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
            try { await launchTask.ConfigureAwait(false); }
            catch (Exception error) { AppLog.Warn("输入接收端启动未完成。", error); }
            throw;
        }
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
        Interlocked.Exchange(ref _armedUntil, allowed ? Environment.TickCount64 + 250 : 0);
        if (!allowed) { _queue.Clear(); TrySend(new InputPacket(InputOperation.Reset)); }
    }
    internal bool TrySend(InputPacket packet)
    {
        if (!Ready || Volatile.Read(ref _disposed) != 0) return false;
        if (!_queue.TryAdd(packet)) { Failure = "Input queue overflow; receiver disconnected."; Dispose(); return false; }
        try { _signal.Release(); }
        catch (SemaphoreFullException) { } // One wake-up drains the already bounded queue.
        return true;
    }

    private async Task RunAsync(uint sessionId)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _signal.WaitAsync(50, _stop.Token).ConfigureAwait(false);
                var allowed = Environment.TickCount64 < Interlocked.Read(ref _armedUntil);
                await ExchangeAsync(new InputPacket(InputOperation.Heartbeat, allowed ? 1 : 0), sessionId).ConfigureAwait(false);
                while (_queue.TryTake(out var packet))
                {
                    if (Environment.TickCount64 >= Interlocked.Read(ref _armedUntil))
                        packet = new InputPacket(InputOperation.Reset);
                    await ExchangeAsync(packet, sessionId).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested) { Failure = error.Message; AppLog.Warn("相对输入通道已断开。", error); }
        }
        finally { Volatile.Write(ref _ready, 0); _pipe.Dispose(); }
    }
    private async Task ExchangeAsync(InputPacket packet, uint sessionId)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        using var memory = new MemoryStream(RelativeInputProtocol.PacketBytes);
        using var writer = new BinaryWriter(memory);
        RelativeInputProtocol.WritePacket(writer, packet);
        await _pipe.WriteAsync(memory.ToArray(), deadline.Token).ConfigureAwait(false);
        var bytes = new byte[RelativeInputProtocol.StatusBytes];
        await _pipe.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var status = RelativeInputStatus.Read(reader);
        if (status.SessionId != sessionId) throw new IOException("Input receiver session changed.");
        Volatile.Write(ref _status, status);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _ready, 0);
        _stop.Cancel();
        _pipe.Dispose();
        if (Volatile.Read(ref _started) == 0) _startup.TrySetResult();
        _ = _startup.Task.ContinueWith(async _ =>
        {
            await _loop.ConfigureAwait(false);
            _signal.Dispose();
            _stop.Dispose();
        }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
}
