using System;
using System.Drawing;
using DesktopTwin.Models;

namespace DesktopTwin.Interop;

/// <summary>将可用区域和本机 DPI 转换为 RDP 显示请求，尺寸始终使用物理像素。</summary>
internal readonly record struct RdpDisplaySettings(Size DesktopSize, uint ScaleFactor)
{
    internal static RdpDisplaySettings FromViewport(double width, double height, double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1;
        var pixelWidth = (int)Math.Clamp(Math.Round(width * dpiScale),
            SessionSettings.MinDesktopWidth, SessionSettings.MaxDesktopWidth);
        var pixelHeight = (int)Math.Clamp(Math.Round(height * dpiScale),
            SessionSettings.MinDesktopHeight, SessionSettings.MaxDesktopHeight);
        return new(new Size(pixelWidth, pixelHeight), (uint)Math.Clamp(Math.Round(dpiScale * 100), 100, 500));
    }

    // RDP 需要毫米尺寸；这里按有效 DPI 估算，不将其当作显示器实测物理尺寸。
    internal uint PhysicalWidth => (uint)Math.Max(10, Math.Round(DesktopSize.Width * 25.4 / (96 * ScaleFactor / 100d)));
    internal uint PhysicalHeight => (uint)Math.Max(10, Math.Round(DesktopSize.Height * 25.4 / (96 * ScaleFactor / 100d)));
}
