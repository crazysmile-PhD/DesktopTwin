using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
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
                    using (var io = new ReceiverPipeStream(pipe))
                    {
                        var hello = io.StatusBytes;
                        RelativeInputProtocol.WriteInt32(hello, 0, RelativeInputProtocol.Version);
                        RelativeInputProtocol.WriteInt32(hello, 4, session);
                        RelativeInputProtocol.WriteInt32(hello, 8, Process.GetCurrentProcess().Id);
                        io.Write(hello, 0, 12);
                        ReceiverLoop.Run(io, session, lease);
                        return 0;
                    }
                }
                catch (Exception error)
                {
                    if (error is IOException || error is ObjectDisposedException || error is Win32Exception
                        || error is UnauthorizedAccessException || error is TimeoutException) return 1;
                    return 4;
                }
            }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    }
}
