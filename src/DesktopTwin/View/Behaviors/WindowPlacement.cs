using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DesktopTwin.View.Behaviors;

/// <summary>在物理像素坐标中恢复窗口，保留完整标题栏和可拖动区域。</summary>
internal static class WindowPlacement
{
    internal static Rectangle Constrain(Rectangle bounds, IReadOnlyList<Rectangle> workAreas,
        bool fitWindow, double dpiScale = 1)
    {
        if (workAreas.Count == 0) return bounds;
        var captionHeight = Math.Max(1, (int)Math.Ceiling(32 * dpiScale));
        var captionWidth = Math.Min(bounds.Width, Math.Max(1, (int)Math.Ceiling(120 * dpiScale)));
        var caption = new Rectangle(bounds.X, bounds.Y, bounds.Width, captionHeight);
        if (!fitWindow && workAreas.Any(area =>
            Rectangle.Intersect(caption, area) is var visible
            && visible.Height >= captionHeight && visible.Width >= captionWidth)) return bounds;

        // 优先选择窗口实际覆盖最多的屏幕；完全离屏时选择最近的工作区。
        var target = workAreas.OrderByDescending(area =>
                (long)Rectangle.Intersect(bounds, area).Width * Rectangle.Intersect(bounds, area).Height)
            .ThenBy(area => DistanceSquared(bounds.Location, area)).First();
        var width = fitWindow ? Math.Min(bounds.Width, target.Width) : bounds.Width;
        var height = fitWindow ? Math.Min(bounds.Height, target.Height) : bounds.Height;
        var minLeft = fitWindow ? target.Left : target.Left - width + captionWidth;
        var maxLeft = fitWindow ? target.Right - width : target.Right - captionWidth;
        var maxTop = target.Bottom - (fitWindow ? height : Math.Min(height, captionHeight));
        return new Rectangle(Math.Clamp(bounds.Left, minLeft, Math.Max(minLeft, maxLeft)),
            Math.Clamp(bounds.Top, target.Top, Math.Max(target.Top, maxTop)), width, height);
    }

    private static double DistanceSquared(Point point, Rectangle area)
    {
        var x = Math.Max(area.Left - (double)point.X, Math.Max(0, point.X - (double)area.Right));
        var y = Math.Max(area.Top - (double)point.Y, Math.Max(0, point.Y - (double)area.Bottom));
        return x * x + y * y;
    }
}
