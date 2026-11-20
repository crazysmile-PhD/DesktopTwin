using System;
using System.Windows;
using System.Windows.Controls;

namespace DesktopTwin.View;

/// <summary>
/// 在可用区域内等比居中安排桌面宿主，不改变外层窗口的尺寸或系统分屏位置。
/// </summary>
public sealed class DesktopViewport : Decorator
{
    /// <summary>
    /// 分身桌面分辨率决定的宽高比。
    /// </summary>
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(DesktopViewport),
        new FrameworkPropertyMetadata(16d / 9d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => value is double ratio && double.IsFinite(ratio) && ratio > 0);

    /// <summary>
    /// 分身桌面分辨率决定的宽高比。
    /// </summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(FitSize(constraint));
        return Child?.DesiredSize ?? new Size();
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var contentSize = FitSize(arrangeSize);
        // WindowsFormsHost 承载独立的 HWND，必须改变它的实际布局矩形。
        // Viewbox/RenderTransform 只缩放 WPF 视觉，不能保证原生画面与输入坐标一致。
        Child?.Arrange(new Rect(
            new Point((arrangeSize.Width - contentSize.Width) / 2,
                (arrangeSize.Height - contentSize.Height) / 2), contentSize));
        return arrangeSize;
    }

    private Size FitSize(Size available)
    {
        if (double.IsPositiveInfinity(available.Width))
        {
            return double.IsPositiveInfinity(available.Height)
                ? available
                : new Size(available.Height * AspectRatio, available.Height);
        }

        var width = Math.Min(available.Width, available.Height * AspectRatio);
        return new Size(width, width / AspectRatio);
    }
}
