using System;
using System.Runtime.InteropServices;
using DesktopTwin.Services;

namespace DesktopTwin.Interop;

/// <summary>连接前请求本机图形优化；可选属性不受支持时继续连接。</summary>
internal static class RdpLocalGraphicsOptions
{
    /// <summary>仅报告属性写入是否被接受，不代表运行时协商成功。</summary>
    internal readonly record struct Acceptance(bool FrameBuffer, bool HardwareDecoding);

    /// <summary>连接前尝试两项独立的优化，失败时保留控件原有行为。</summary>
    internal static Acceptance Apply(Action<string, bool> write)
    {
        bool TryWrite(string property)
        {
            try
            {
                write(property, true);
                return true;
            }
            catch (Exception exception) when (exception is COMException or MissingMemberException
                or InvalidCastException or ArgumentException)
            {
                AppLog.Warn($"RDP 可选图形属性 {property} 不被接受，继续连接。", exception);
                return false;
            }
        }

        return new Acceptance(TryWrite("EnableFrameBufferRedirection"), TryWrite("EnableHardwareMode"));
    }
}
