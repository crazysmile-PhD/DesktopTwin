using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.Models;
using Microsoft.Xaml.Behaviors;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace DesktopTwin.View.Behaviors;

/// <summary>
/// 负责窗口尺寸切换与位置记忆：
/// 1. <see cref="ResizeRequest"/> 变化时按 <see cref="TargetWidth"/> 与 <see cref="AspectRatio"/> 重算窗口尺寸；
/// 2. 普通窗口与小窗模式各自记忆位置，并保证标题栏始终有可见部分留在某个屏幕内。
/// </summary>
public sealed class WindowResizeBehavior : Behavior<FrameworkElement>
{
    /// <summary>
    /// 目标窗口宽度（设备无关像素）。
    /// </summary>
    public static readonly DependencyProperty TargetWidthProperty =
        DependencyProperty.Register(
            nameof(TargetWidth),
            typeof(double),
            typeof(WindowResizeBehavior),
            new PropertyMetadata(double.NaN));

    /// <summary>
    /// 内容区宽高比。
    /// </summary>
    public static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(WindowResizeBehavior),
            new PropertyMetadata(double.NaN));

    /// <summary>
    /// 尺寸重算请求号。值变化即触发一次重算，避免依赖绑定值的语义。
    /// </summary>
    public static readonly DependencyProperty ResizeRequestProperty =
        DependencyProperty.Register(
            nameof(ResizeRequest),
            typeof(int),
            typeof(WindowResizeBehavior),
            new PropertyMetadata(0, OnResizeRequestChanged));

    /// <summary>
    /// 当前是否处于小窗模式。
    /// </summary>
    public static readonly DependencyProperty IsSmallWindowModeProperty =
        DependencyProperty.Register(
            nameof(IsSmallWindowMode),
            typeof(bool),
            typeof(WindowResizeBehavior),
            new PropertyMetadata(false));

    /// <summary>
    /// 普通窗口模式下的记忆位置。
    /// </summary>
    public static readonly DependencyProperty NormalWindowPositionProperty =
        DependencyProperty.Register(
            nameof(NormalWindowPosition),
            typeof(WindowPosition),
            typeof(WindowResizeBehavior));

    /// <summary>
    /// 小窗模式下的记忆位置。
    /// </summary>
    public static readonly DependencyProperty SmallWindowPositionProperty =
        DependencyProperty.Register(
            nameof(SmallWindowPosition),
            typeof(WindowPosition),
            typeof(WindowResizeBehavior));

    private Window? _window;
    private DispatcherTimer? _savePositionTimer;
    private bool _activeSmallWindowMode;
    private bool _isApplyingPlacement;
    private HwndSource? _source;
    private bool _positionCorrectionPending;

    /// <summary>
    /// 目标窗口宽度。
    /// </summary>
    public double TargetWidth
    {
        get => (double)GetValue(TargetWidthProperty);
        set => SetValue(TargetWidthProperty, value);
    }

    /// <summary>
    /// 内容区宽高比。
    /// </summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>
    /// 尺寸重算请求号。
    /// </summary>
    public int ResizeRequest
    {
        get => (int)GetValue(ResizeRequestProperty);
        set => SetValue(ResizeRequestProperty, value);
    }

    /// <summary>
    /// 当前是否处于小窗模式。
    /// </summary>
    public bool IsSmallWindowMode
    {
        get => (bool)GetValue(IsSmallWindowModeProperty);
        set => SetValue(IsSmallWindowModeProperty, value);
    }

    /// <summary>
    /// 普通窗口模式下的记忆位置。
    /// </summary>
    public WindowPosition? NormalWindowPosition
    {
        get => (WindowPosition?)GetValue(NormalWindowPositionProperty);
        set => SetValue(NormalWindowPositionProperty, value);
    }

    /// <summary>
    /// 小窗模式下的记忆位置。
    /// </summary>
    public WindowPosition? SmallWindowPosition
    {
        get => (WindowPosition?)GetValue(SmallWindowPositionProperty);
        set => SetValue(SmallWindowPositionProperty, value);
    }

    /// <inheritdoc />
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Loaded += OnAssociatedObjectLoaded;
        AssociatedObject.Unloaded += OnAssociatedObjectUnloaded;

        if (AssociatedObject.IsLoaded)
        {
            AttachWindow();
        }
    }

    /// <inheritdoc />
    protected override void OnDetaching()
    {
        AssociatedObject.Loaded -= OnAssociatedObjectLoaded;
        AssociatedObject.Unloaded -= OnAssociatedObjectUnloaded;
        DetachWindow();
        base.OnDetaching();
    }

    private static void OnResizeRequestChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (Equals(e.OldValue, e.NewValue))
        {
            return;
        }

        ((WindowResizeBehavior)dependencyObject).ResizeWindow();
    }

    private void OnAssociatedObjectLoaded(object sender, RoutedEventArgs e)
    {
        AttachWindow();
    }

    private void OnAssociatedObjectUnloaded(object sender, RoutedEventArgs e)
    {
        DetachWindow();
    }

    private void AttachWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _window = Window.GetWindow(AssociatedObject);
        if (_window is null)
        {
            return;
        }

        _activeSmallWindowMode = IsSmallWindowMode;
        _window.LocationChanged += OnWindowLocationChanged;
        _window.IsVisibleChanged += OnWindowIsVisibleChanged;
        _window.Closing += OnWindowClosing;
        _savePositionTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _savePositionTimer.Tick += OnSavePositionTimerTick;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WindowProc);
        // 首次恢复前不能保存当前临时位置，否则会覆盖待恢复的旧位置。
        _isApplyingPlacement = true;
        _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ResizeWindowCore));
    }

    private void DetachWindow()
    {
        SaveActiveWindowPosition();
        if (_savePositionTimer is not null)
        {
            _savePositionTimer.Stop();
            _savePositionTimer.Tick -= OnSavePositionTimerTick;
            _savePositionTimer = null;
        }

        if (_window is not null)
        {
            _window.LocationChanged -= OnWindowLocationChanged;
            _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
            _window.Closing -= OnWindowClosing;
        }

        _isApplyingPlacement = false;
        _source?.RemoveHook(WindowProc);
        _source = null;
        _window = null;
    }

    private void ResizeWindow()
    {
        AttachWindow();
        if (_window is null || _isApplyingPlacement)
        {
            return;
        }

        if (_window.WindowState != WindowState.Normal)
        {
            _window.WindowState = WindowState.Normal;
        }

        _savePositionTimer?.Stop();
        SaveWindowPosition(_activeSmallWindowMode);
        _activeSmallWindowMode = IsSmallWindowMode;
        _isApplyingPlacement = true;

        _ = _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(ResizeWindowCore));
    }

    private void ResizeWindowCore()
    {
        if (_window is null)
        {
            return;
        }

        if (double.IsFinite(TargetWidth) &&
            TargetWidth > 0 &&
            double.IsFinite(AspectRatio) &&
            AspectRatio > 0 &&
            AssociatedObject.ActualWidth > 0 &&
            AssociatedObject.ActualHeight > 0)
        {
            var nonContentWidth = Math.Max(0, _window.ActualWidth - AssociatedObject.ActualWidth);
            var nonContentHeight = Math.Max(0, _window.ActualHeight - AssociatedObject.ActualHeight);
            var targetWindowWidth = Math.Max(_window.MinWidth, TargetWidth);
            if (double.IsFinite(_window.MaxWidth))
            {
                targetWindowWidth = Math.Min(targetWindowWidth, _window.MaxWidth);
            }

            var targetContentWidth = Math.Max(1, targetWindowWidth - nonContentWidth);
            var targetWindowHeight = Math.Max(
                _window.MinHeight,
                targetContentWidth / AspectRatio + nonContentHeight);
            if (double.IsFinite(_window.MaxHeight))
            {
                targetWindowHeight = Math.Min(targetWindowHeight, _window.MaxHeight);
            }

            _window.Width = targetWindowWidth;
            _window.Height = targetWindowHeight;
        }

        _ = _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(CompleteResize));
    }

    private void CompleteResize()
    {
        try
        {
            _window?.UpdateLayout();
            RestoreWindowPosition(_activeSmallWindowMode);
        }
        finally
        {
            _isApplyingPlacement = false;
        }
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (_isApplyingPlacement || _window?.WindowState != WindowState.Normal || _savePositionTimer is null)
        {
            return;
        }

        _savePositionTimer.Stop();
        _savePositionTimer.Start();
    }

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            SaveActiveWindowPosition();
        }
        else
        {
            SchedulePositionCorrection(fitWindow: true);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        SaveActiveWindowPosition();
    }

    private void OnSavePositionTimerTick(object? sender, EventArgs e)
    {
        _savePositionTimer?.Stop();
        CorrectCurrentPosition(fitWindow: false);
        SaveActiveWindowPosition();
    }

    private void SaveActiveWindowPosition()
    {
        _savePositionTimer?.Stop();
        SaveWindowPosition(_activeSmallWindowMode);
    }

    private void SaveWindowPosition(bool isSmallWindowMode)
    {
        if (_isApplyingPlacement ||
            _window?.WindowState != WindowState.Normal ||
            !TryGetWindowBounds(out var bounds))
        {
            return;
        }

        var corrected = ConstrainBounds(bounds, fitWindow: false);
        UpdateStoredPosition(isSmallWindowMode, corrected.Left, corrected.Top);
    }

    private void RestoreWindowPosition(bool isSmallWindowMode)
    {
        if (!TryGetWindowBounds(out var windowBounds))
        {
            return;
        }

        var storedPosition = isSmallWindowMode ? SmallWindowPosition : NormalWindowPosition;
        var desiredLeft = storedPosition?.Left ?? windowBounds.Left;
        var desiredTop = storedPosition?.Top ?? windowBounds.Top;
        var correctedPosition = ConstrainBounds(new DrawingRectangle(
            desiredLeft, desiredTop, windowBounds.Width, windowBounds.Height), fitWindow: true);

        var handle = new WindowInteropHelper(_window!).Handle;
        if (handle != IntPtr.Zero &&
            windowBounds != correctedPosition)
        {
            WindowNativeMethods.SetWindowBounds(handle, correctedPosition.Left, correctedPosition.Top,
                correctedPosition.Width, correctedPosition.Height);
        }

        if (storedPosition is not null)
        {
            UpdateStoredPosition(isSmallWindowMode, correctedPosition.Left, correctedPosition.Top);
        }
    }

    private bool TryGetWindowBounds(out DrawingRectangle bounds)
    {
        bounds = DrawingRectangle.Empty;
        if (_window is null)
        {
            return false;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (!WindowNativeMethods.TryGetWindowRect(handle, out var rect))
        {
            return false;
        }

        bounds = DrawingRectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void UpdateStoredPosition(bool isSmallWindowMode, int left, int top)
    {
        var currentPosition = isSmallWindowMode ? SmallWindowPosition : NormalWindowPosition;
        if (currentPosition?.Matches(new WindowPosition(left, top)) == true)
        {
            return;
        }

        var newPosition = new WindowPosition(left, top);
        var positionProperty = isSmallWindowMode
            ? SmallWindowPositionProperty
            : NormalWindowPositionProperty;
        SetCurrentValue(positionProperty, newPosition);
        BindingOperations.GetBindingExpression(this, positionProperty)?.UpdateSource();
    }

    private DrawingRectangle ConstrainBounds(DrawingRectangle bounds, bool fitWindow)
    {
        var dpi = _window is null ? 1 : VisualTreeHelper.GetDpi(_window).DpiScaleY;
        return WindowPlacement.Constrain(bounds,
            FormsScreen.AllScreens.Select(screen => screen.WorkingArea).ToArray(), fitWindow, dpi);
    }

    private void SchedulePositionCorrection(bool fitWindow)
    {
        if (_positionCorrectionPending) return;
        _positionCorrectionPending = true;
        _ = AssociatedObject.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _positionCorrectionPending = false;
            CorrectCurrentPosition(fitWindow);
        }));
    }

    private void CorrectCurrentPosition(bool fitWindow)
    {
        if (_isApplyingPlacement || _window?.WindowState != WindowState.Normal
            || _window.ResizeMode == ResizeMode.NoResize || !TryGetWindowBounds(out var bounds)) return;
        var corrected = ConstrainBounds(bounds, fitWindow);
        if (corrected == bounds) return;
        _isApplyingPlacement = true;
        try
        {
            WindowNativeMethods.SetWindowBounds(new WindowInteropHelper(_window).Handle,
                corrected.Left, corrected.Top, corrected.Width, corrected.Height);
            UpdateStoredPosition(_activeSmallWindowMode, corrected.Left, corrected.Top);
        }
        finally { _isApplyingPlacement = false; }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_window?.ResizeMode == ResizeMode.NoResize || _window?.WindowState != WindowState.Normal) return 0;
        if (message == 0x0216 && lParam != 0) // WM_MOVING: constrain the proposed rectangle before it is applied.
        {
            var rect = Marshal.PtrToStructure<WindowNativeMethods.NativeRect>(lParam);
            var proposed = DrawingRectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var corrected = ConstrainBounds(proposed, fitWindow: false);
            rect.Left = corrected.Left; rect.Top = corrected.Top;
            rect.Right = corrected.Right; rect.Bottom = corrected.Bottom;
            Marshal.StructureToPtr(rect, lParam, false);
            handled = true;
            return 1;
        }
        if (message is 0x007E or 0x02E0 or 0x0232) // Display change, DPI change, end move/resize.
            SchedulePositionCorrection(fitWindow: message != 0x0232);
        return 0;
    }
}
