using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Launching;

namespace DesktopTwinSessionAgent
{
    // A short-lived worker in the destination desktop, separate from the hotkey agent.
    // It waits for the host to remove the bootstrap task BEFORE receiving a launch request.
    internal static class SessionLaunchWorker
    {
        internal static int Run(string pipeName, int parentId)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification))
            {
                try
                {
                    pipe.Connect(SessionLaunchProtocol.ConnectionMilliseconds);
                    uint serverId;
                    if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out serverId)
                        || serverId != parentId) return 2;

                    var reader = new BinaryReader(pipe, Encoding.UTF8, true);
                    var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
                    // A host that dies before authorizing the launch cannot strand this worker.
                    var request = Task.Factory.StartNew(delegate
                    {
                        if (reader.ReadInt32() != SessionLaunchProtocol.Version)
                            throw new IOException("Unsupported launch protocol.");
                        return new Request(reader.ReadString(), reader.ReadString(), reader.ReadString(),
                            reader.ReadInt32(), reader.ReadBoolean(), reader.ReadInt32());
                    });
                    if (!request.Wait(SessionLaunchProtocol.ConnectionMilliseconds)) return 3;
                    var value = request.Result;
                    if (Process.GetCurrentProcess().SessionId != value.SessionId) return 4;
                    if (value.ObservationMilliseconds < 1
                        || value.ObservationMilliseconds > SessionLaunchProtocol.ObservationMilliseconds) return 4;

                    // EOF/cancellation from the host stops observation, never starts another target.
                    var disconnected = Task.Factory.StartNew(delegate { return pipe.ReadByte(); });
                    return Launch(value, writer, disconnected);
                }
                catch (Exception) { return 1; } // Host sees EOF, never a fabricated success.
            }
        }

        private sealed class Request
        {
            internal readonly string Path, Arguments, Directory;
            internal readonly int SessionId;
            internal readonly bool Agent;
            internal readonly int ObservationMilliseconds;
            internal Request(string path, string arguments, string directory, int sessionId, bool agent, int milliseconds)
            { Path = path; Arguments = arguments; Directory = directory; SessionId = sessionId; Agent = agent;
                ObservationMilliseconds = milliseconds; }
        }

        private static int Launch(Request request, BinaryWriter writer, Task disconnected)
        {
            Process target = null;
            int pid = 0;
            long started = 0;
            string eventName = @"Local\DesktopTwin.AgentReady." + Guid.NewGuid().ToString("N");
            using (var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
            {
                try
                {
                    if (disconnected.IsCompleted) return 5;
                    var info = new ProcessStartInfo(request.Path, request.Arguments
                        + (request.Agent ? " --ready-event " + eventName : string.Empty));
                    info.WorkingDirectory = request.Directory;
                    info.UseShellExecute = false;
                    target = Process.Start(info);
                    if (target == null) throw new InvalidOperationException("未取得目标进程。");
                    pid = target.Id;
                    started = target.StartTime.ToUniversalTime().Ticks;
                    if (target.SessionId != request.SessionId)
                        throw new InvalidOperationException("目标进程不在请求的会话中。");

                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < request.ObservationMilliseconds)
                    {
                        if (disconnected.IsCompleted) return 5;
                        if (target.HasExited)
                            throw new InvalidOperationException("目标进程在就绪确认前退出，退出码 " + target.ExitCode + "。");
                        target.Refresh();
                        IntPtr window = request.Agent ? IntPtr.Zero : FindResponsiveWindow(pid);
                        bool ready = request.Agent ? readyEvent.WaitOne(0) : window != IntPtr.Zero;
                        if (ready && !target.HasExited)
                        {
                            SessionLaunchProtocol.WriteResult(writer, true, pid, request.SessionId,
                                started, window.ToInt64(), string.Empty);
                            return 0;
                        }
                        Thread.Sleep(50);
                    }
                    throw new TimeoutException(request.Agent
                        ? "代理未确认热键注册及消息循环就绪。"
                        : "进程已创建，但未在确认期限内观察到可见且可响应的窗口；请检查分身桌面，避免重复启动。");
                }
                catch (Exception error)
                {
                    SessionLaunchProtocol.WriteResult(writer, false, pid, request.SessionId, started, 0,
                        error.GetBaseException().Message);
                    return 1;
                }
                finally { if (target != null) target.Dispose(); }
            }
        }

        private static bool IsResponsiveWindow(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindowVisible(window)) return false;
            IntPtr result;
            // A visible HWND alone is not evidence that its UI thread responds.
            return SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 0x2, 200, out result) != IntPtr.Zero;
        }

        private static IntPtr FindResponsiveWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner == pid && IsResponsiveWindow(window))
                { found = window; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
