using System;

namespace DesktopTwinSessionLaunchWorker
{
    /// <summary>只接受主程序的一次性启动请求；没有热键或常驻模式。</summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            int parentId;
            if (args.Length != 3 || args[0] != "--launch-worker"
                || !int.TryParse(args[2], out parentId) || parentId <= 0) return 2;
            return SessionLaunchWorker.Run(args[1], parentId);
        }
    }
}
