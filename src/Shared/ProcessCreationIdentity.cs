using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopTwin.Launching
{
    // Share the raw Windows FILETIME, without per-runtime/local-time DateTime conversions.
    internal static class ProcessCreationIdentity
    {
        internal static string ReadImagePath(int processId)
        {
            var handle = OpenProcess(0x1000, false, processId);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                uint length = 32768;
                var path = new char[length];
                if (!QueryFullProcessImageName(handle, 0, path, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new string(path, 0, (int)length);
            }
            finally { CloseHandle(handle); }
        }
        internal static long Read(int processId)
        {
            var handle = OpenProcess(0x1000, false, processId);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                long creation, exit, kernel, user;
                if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return creation;
            }
            finally { CloseHandle(handle); }
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] path, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
