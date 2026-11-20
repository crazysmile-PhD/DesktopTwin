using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Launching;

namespace DesktopTwin.Services;

internal sealed class SessionLaunchChannel : IDisposable
{
    internal string PipeName { get; } = "DesktopTwin.Launch." + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream _pipe;
    private readonly string _workerPath;
    private readonly uint _sessionId;

    internal SessionLaunchChannel(string workerPath, uint sessionId)
    {
        _workerPath = workerPath;
        _sessionId = sessionId;
        // CurrentUserOnly also enforces elevation equality on Windows, which would reject
        // the intentionally unelevated worker. Validate the peer's PID, path and user instead.
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    internal async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SessionLaunchProtocol.ConnectionMilliseconds);
        try
        {
            await _pipe.WaitForConnectionAsync(budget.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
                throw new IOException("无法确认启动代理的进程身份。");
            using var process = Process.GetProcessById(checked((int)pid));
            if ((uint)process.SessionId != _sessionId
                || !string.Equals(ProcessCreationIdentity.ReadImagePath(process.Id), _workerPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("启动代理身份或会话不匹配。");
            var expectedUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            System.Security.Principal.SecurityIdentifier? peerUser = null;
            _pipe.RunAsClient(() => peerUser = System.Security.Principal.WindowsIdentity.GetCurrent().User);
            if (expectedUser is null || expectedUser != peerUser)
                throw new InvalidOperationException("启动代理用户身份不匹配。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("未能连接到分身会话内的启动代理。"); }
    }

    internal async Task LaunchAndConfirmAsync(string path, string arguments, string directory,
        CancellationToken cancellationToken,
        int observationMilliseconds = SessionLaunchProtocol.ObservationMilliseconds, bool requireWindow = true)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(observationMilliseconds + 5000);
        // Closing the channel unblocks synchronous Framework-compatible protocol reads.
        using var cancel = budget.Token.Register(Dispose);
        try
        {
            await Task.Run(() =>
            {
                using var writer = new BinaryWriter(_pipe, Encoding.UTF8, leaveOpen: true);
                using var reader = new BinaryReader(_pipe, Encoding.UTF8, leaveOpen: true);
                SessionLaunchProtocol.WriteRequest(writer, path, arguments, directory, checked((int)_sessionId),
                    observationMilliseconds, requireWindow);
                if (reader.ReadInt32() != SessionLaunchProtocol.Version)
                    throw new IOException("启动代理协议版本不匹配。");
                var ready = reader.ReadBoolean();
                var pid = reader.ReadInt32();
                var session = reader.ReadInt32();
                var started = reader.ReadInt64();
                var window = reader.ReadInt64();
                var error = reader.ReadString();
                if (!ready) throw new InvalidOperationException($"启动未确认（PID {pid}）：{error}");
                using var target = Process.GetProcessById(pid);
                var exited = target.HasExited;
                var observedSession = target.SessionId;
                var observedStart = exited ? 0 : ProcessCreationIdentity.Read(pid);
                // The loader's MainModule list can still be empty immediately after CreateProcess.
                var pathMatches = !exited && string.Equals(ProcessCreationIdentity.ReadImagePath(pid), path, StringComparison.OrdinalIgnoreCase);
                if (exited || session != _sessionId || observedSession != session
                    || observedStart != started || !pathMatches
                    || (requireWindow && window == 0))
                    throw new SessionLaunchReceiptMismatchException($" PID={pid}, exited={exited}, "
                        + $"session={session}/{observedSession}, creationFileTime={started}/{observedStart}, "
                        + $"pathMatch={pathMatches}, window=0x{window:X}.");
                AppLog.Info($"已确认会话 {session} 中的 {path} {(requireWindow ? "窗口就绪" : "进程创建（能力待握手）")}，PID {pid}，"
                    + $"可响应窗口 0x{window:X}。");
            }, budget.Token).ConfigureAwait(false);
        }
        catch (Exception) when (budget.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("启动确认超时；请检查分身桌面，避免重复启动。");
        }
    }

    internal static string EnsureWorkerInstalled(string? baseDirectory = null) =>
        EnsureEmbeddedProgram("DesktopTwin.LaunchWorker.exe", baseDirectory);

    internal static string EnsureEmbeddedProgram(string resourceName, string? baseDirectory = null)
    {
        using var stream = typeof(SessionLaunchChannel).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("缺少内嵌启动代理。");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var directory = Path.Combine(baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTwin", "LaunchWorker"), hash);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, resourceName);
        if (File.Exists(path))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), SHA256.HashData(bytes)))
                throw new IOException("启动代理文件校验失败。");
            return path;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path))
            {
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw;
            }
        }
        finally { File.Delete(temporary); }
        return path;
    }

    public void Dispose() => _pipe.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
}
