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
                var lease = new InputLease(ReceiverInput.Inject);
                try
                {
                    pipe.Connect(15000);
                    uint server;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out server)
                        || server != parent) return 3;
                    var reader = new BinaryReader(pipe);
                    var writer = new BinaryWriter(pipe);
                    var statusBytes = new byte[RelativeInputProtocol.StatusBytes];
                    var statusBuffer = new MemoryStream(statusBytes, true);
                    var statusWriter = new BinaryWriter(statusBuffer);
                    writer.Write(RelativeInputProtocol.Version);
                    writer.Write(session);
                    writer.Write(Process.GetCurrentProcess().Id);
                    writer.Flush();
                    long lastPacket = Stopwatch.GetTimestamp();
                    int received = 0;
                    using (var watchdog = new Timer(delegate
                    {
                        if ((Stopwatch.GetTimestamp() - Interlocked.Read(ref lastPacket)) * 1000 / Stopwatch.Frequency
                            > (Volatile.Read(ref received) == 0 ? 20000 : RelativeInputProtocol.LeaseMilliseconds)) pipe.Dispose();
                    }, null, 250, 250))
                    {
                        while (true)
                        {
                            var packet = RelativeInputProtocol.ReadPacket(reader);
                            Interlocked.Exchange(ref lastPacket, Stopwatch.GetTimestamp());
                            Interlocked.Exchange(ref received, 1);
                            lease.Apply(packet);
                            statusBuffer.Position = 0;
                            ReceiverInput.WriteStatus(statusWriter, session, lease);
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
