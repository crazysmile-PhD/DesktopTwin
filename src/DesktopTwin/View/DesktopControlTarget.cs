using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopTwin.Diagnostics;
using DesktopTwin.Interop;
using DesktopTwin.Services;
using DesktopTwin.ViewModel;

namespace DesktopTwin.View;

/// <summary>将测试请求限制在一个分身视窗内，保持测试协议独立于主窗口实现。</summary>
internal sealed class DesktopControlTarget(MainWindow window, SettingsService settings)
{
    private RdpActiveXHost RdpHost => window.RdpHost;
    /// <summary>测试桥接仅调度现有命令及受控输入，所有窗口/控件访问保留在 UI 线程。</summary>
    internal async Task<object> HandleControlRequestAsync(DesktopControlRequest request, CancellationToken cancellationToken)
    {
        if (!window.Dispatcher.CheckAccess())
            return await window.Dispatcher.InvokeAsync(() => HandleControlRequestAsync(request, cancellationToken)).Task.Unwrap().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        var viewModel = window.DataContext as MainWindowViewModel ?? throw new InvalidOperationException("View model is unavailable.");
        switch (request.Command)
        {
            case "state": return CaptureControlState();
            case "connect":
                _ = window.Dispatcher.BeginInvoke(new Action(() => viewModel.StartCommand.Execute(null)));
                return new { accepted = true };
            case "launch_target":
                RequireControlInput();
                _ = window.Dispatcher.BeginInvoke(new Action(() => viewModel.LaunchTargetCommand.Execute(null)));
                return new { accepted = true };
            case "focus":
                window.Activate();
                WindowNativeMethods.SetForegroundWindow(new WindowInteropHelper(window).Handle);
                return new { focused = RdpHost.IsHandleCreated && ChildSessionNativeMethods.TryFocusRdpInputWindow(RdpHost.Handle) };
            case "resize":
                if (window.ResizeMode == ResizeMode.NoResize) throw new InvalidOperationException("Restore windowed mode before a test resize.");
                window.WindowState = WindowState.Normal;
                var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
                var dpi = VisualTreeHelper.GetDpi(window);
                window.Width = Math.Min(request.Width, area.Width / dpi.DpiScaleX);
                window.Height = Math.Min(request.Height, area.Height / dpi.DpiScaleY);
                window.UpdateLayout();
                return CaptureControlState();
            case "key":
            case "mouse":
            case "click":
                await ApplyControlInputAsync(request, cancellationToken);
                return CaptureControlState();
            default: throw new InvalidOperationException("Unknown control command.");
        }
    }

    private async Task ApplyControlInputAsync(DesktopControlRequest request, CancellationToken cancellationToken)
    {
        var rect = RequireControlInput();
        var scan = request.Command == "key" ? DesktopControlRequest.ScanCode(request.Key) : (ushort)0;
        var alt = request.Alt && scan != DesktopControlRequest.ScanCode("ALT");
        try
        {
            if (alt) { DiagnosticInput.Key(0x38, false); await Task.Delay(100, cancellationToken); }
            if (scan != 0)
            {
                DiagnosticInput.Key(scan, false);
                await Task.Delay(request.Milliseconds, cancellationToken);
                return;
            }
            if (request.Command == "click")
            {
                if (request.X < 0 || request.Y < 0 || request.X >= rect.Right - rect.Left || request.Y >= rect.Bottom - rect.Top)
                    throw new ArgumentOutOfRangeException(nameof(request), "Click is outside the RDP surface.");
                DiagnosticInput.MoveTo(rect.Left + request.X, rect.Top + request.Y);
                DiagnosticInput.Mouse(0, 0, click: true);
            }
            else
            {
                var cursor = DiagnosticInput.Cursor();
                if (cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom)
                    throw new InvalidOperationException("Place the pointer inside the RDP surface before relative movement.");
                var dx = Math.Clamp(request.X, rect.Left - cursor.X, rect.Right - 1 - cursor.X);
                var dy = Math.Clamp(request.Y, rect.Top - cursor.Y, rect.Bottom - 1 - cursor.Y);
                DiagnosticInput.Mouse(dx, dy, click: false);
            }
        }
        finally
        {
            if (scan != 0) DiagnosticInput.Key(scan, true);
            if (alt) DiagnosticInput.Key(0x38, true);
        }
    }

    private WindowNativeMethods.NativeRect RequireControlInput()
    {
        if (!RdpHost.LoginComplete || RdpHost.ConnectedState != 1 || settings.Current.Session.WatchMode)
            throw new InvalidOperationException("A logged-in RDP connection in control mode is required.");
        if (ChildSessionNativeMethods.GetForegroundWindow() != new WindowInteropHelper(window).Handle)
            throw new InvalidOperationException("Select the DesktopTwin window before sending test input.");
        if (!WindowNativeMethods.TryGetWindowRect(RdpHost.Handle, out var bounds))
            throw new InvalidOperationException("RDP surface bounds are unavailable.");
        if (!ChildSessionNativeMethods.TryFocusRdpInputWindow(RdpHost.Handle))
            throw new InvalidOperationException("RDP input focus could not be confirmed.");
        return bounds;
    }

    private object CaptureControlState()
    {
        WindowNativeMethods.TryGetWindowRect(new WindowInteropHelper(window).Handle, out var windowBounds);
        var surface = RdpHost.IsHandleCreated && WindowNativeMethods.TryGetWindowRect(RdpHost.Handle, out var rect)
            ? new { left = rect.Left, top = rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top } : null;
        return new
        {
            connected = RdpHost.ConnectedState, loggedIn = RdpHost.LoginComplete,
            watchMode = settings.Current.Session.WatchMode,
            window = new { left = windowBounds.Left, top = windowBounds.Top, width = windowBounds.Right - windowBounds.Left, height = windowBounds.Bottom - windowBounds.Top },
            surface, desktop = RdpHost.ReportedDesktopSize,
            relativeRequested = RdpHost.RelativeMouseModeConfiguredEnabled,
            relativeAccepted = RdpHost.RelativeMouseModeSettingsAccepted,
            relativeReadback = RdpHost.RelativeMouseModeAfterLogin,
            relativeReported = RdpHost.ReportedRelativeMouseMode,
            cursor = DiagnosticInput.CaptureState(),
            inputFocused = RdpHost.IsHandleCreated && ChildSessionNativeMethods.IsRdpInputWindowFocused(RdpHost.Handle)
        };
    }
}
