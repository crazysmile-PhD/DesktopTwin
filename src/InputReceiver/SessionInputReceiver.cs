using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using DesktopTwin.Input;

namespace DesktopTwinInputReceiver
{
    /// <summary>Session-scoped receiver. No command execution, installation, hooks or startup entries.</summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            int parent, session;
            Guid nonce;
            const string prefix = "DesktopTwin.Input.";
            if (args.Length != 4 || args[0] != "--relative-input"
                || !args[1].StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(args[1].Substring(prefix.Length), "N", out nonce)
                || !int.TryParse(args[2], out parent) || parent < 1
                || !int.TryParse(args[3], out session) || session != Process.GetCurrentProcess().SessionId) return 2;
            using (var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
            {
                var input = new ReceiverInput();
                var lease = new InputLease(input.Inject);
                try
                {
                    pipe.Connect(15000);
                    uint server;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out server)
                        || server != parent) return 3;
                    var packetBytes = new byte[RelativeInputProtocol.PacketBytes];
                    var statusBytes = new byte[RelativeInputProtocol.StatusBytes];
                    var hello = new byte[12];
                    RelativeInputProtocol.WriteInt32(hello, 0, RelativeInputProtocol.Version);
                    RelativeInputProtocol.WriteInt32(hello, 4, session);
                    RelativeInputProtocol.WriteInt32(hello, 8, Process.GetCurrentProcess().Id);
                    pipe.Write(hello, 0, hello.Length);
                    long expiresAt = Stopwatch.GetTimestamp() + 20 * Stopwatch.Frequency;
                    Timer watchdog = null;
                    watchdog = new Timer(delegate
                    {
                        var deadline = Interlocked.Read(ref expiresAt);
                        if (deadline == long.MaxValue) return;
                        var remaining = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                        if (remaining <= 0) { pipe.Dispose(); return; }
                        try { watchdog.Change((int)remaining + 1, Timeout.Infinite); }
                        catch (ObjectDisposedException) { }
                    }, null, 20000, Timeout.Infinite);
                    using (watchdog)
                    {
                        var firstPacket = true;
                        while (true)
                        {
                            var packet = RelativeInputProtocol.ReadPacket(pipe, packetBytes);
                            var receivedAt = Stopwatch.GetTimestamp();
                            var wasArmed = lease.Armed;
                            lease.Apply(packet);
                            Interlocked.Exchange(ref expiresAt, lease.Armed
                                ? receivedAt + RelativeInputProtocol.LeaseMilliseconds * Stopwatch.Frequency / 1000
                                : long.MaxValue);
                            // Disarmed peers hold no injected state: block on the pipe until input or EOF.
                            // Active traffic only renews the deadline; one callback observes its latest value.
                            if (!lease.Armed && (wasArmed || firstPacket)) watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                            else if (lease.Armed && !wasArmed) watchdog.Change(RelativeInputProtocol.LeaseMilliseconds, Timeout.Infinite);
                            firstPacket = false;
                            ReceiverInput.WriteStatus(statusBytes, session, lease);
                            pipe.Write(statusBytes, 0, statusBytes.Length);
                            if (packet.Operation == InputOperation.Close) return 0;
                        }
                    }
                }
                catch (Exception error)
                {
                    if (error is IOException || error is ObjectDisposedException || error is Win32Exception
                        || error is UnauthorizedAccessException || error is TimeoutException) return 1;
                    return 4;
                }
                finally
                {
                    try { lease.Reset(); }
                    catch (Win32Exception) { } // Process/session shutdown may already have removed the input desktop.
                }
            }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    }
}
