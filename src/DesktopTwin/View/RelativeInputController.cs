using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopTwin.Input;
using DesktopTwin.Interop;
using DesktopTwin.Services;
using DesktopTwin.ViewModel;

namespace DesktopTwin.View;

/// <summary>Coordinates foreground capture with a session receiver; never captures a desktop/menu with a visible cursor.</summary>
internal sealed class RelativeInputController : IDisposable
{
    private readonly MainWindow _window;
    private readonly SettingsService _settings;
    private readonly RelativeMouseCapture _capture;
    private readonly DispatcherTimer _poll;
    private RelativeInputSession? _session;
    private CancellationTokenSource? _starting;
    private uint? _sessionId;
    private readonly CaptureReleaseGesture _gesture = new();
    private bool _failed;
    private bool _disposed;
    internal bool Captured => _capture.Active;
    internal bool Ready => _session?.Ready == true;
    internal RelativeInputStatus? Status => _session?.Status;
    internal string? Failure { get; private set; }
    internal long RawMoves => _capture.RawMoves;

    internal RelativeInputController(MainWindow window, SettingsService settings)
    {
        _window = window;
        _settings = settings;
        _capture = new RelativeMouseCapture(Send, ObserveGesture);
        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(32) };
        _poll.Tick += Poll;
        window.RdpHost.LoginCompleted += OnLogin;
        window.Deactivated += OnDeactivated;
        window.IsVisibleChanged += OnVisibility;
        _poll.Start();
    }
    private void OnLogin(object? sender, EventArgs e) { _failed = false; StopSession(); }
    private void OnDeactivated(object? sender, EventArgs e) => Release();
    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs e) { if (!_window.IsVisible) Release(); }
    private bool Send(InputPacket packet) => _session?.TrySend(packet) == true;
    internal bool SendKey(ushort scan, bool up) => Ready && Send(new InputPacket(InputOperation.Key, scan, up ? 0 : 1));
    internal bool Move(int x, int y) => Captured && Send(new InputPacket(InputOperation.Move, x, y));
    internal bool Click() => Captured && Send(new InputPacket(InputOperation.Button, 1, 1))
        && Send(new InputPacket(InputOperation.Button, 1, 0));

    private void ObserveGesture(int message, int key, bool alt)
    {
        _gesture.ObserveMessage(message, key, alt || Status?.CursorVisible == true);
        if (_gesture.Released) ReleasePointer();
    }

    private void Poll(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try { Update(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or System.IO.IOException)
        { Fail(error); }
    }
    private void Update()
    {
        var enabled = _settings.Current.Session.RelativeMouseMode;
        var id = ChildSessionNativeMethods.TryGetChildSessionId();
        if (!enabled || !_window.RdpHost.LoginComplete || _window.RdpHost.ConnectedState != 1 || id is null)
        { StopSession(); return; }
        if (_sessionId != id && _session is not null) StopSession();
        if (_session is null && _starting is null && !_failed) { _ = StartSessionAsync(id.Value); return; }
        if (_session?.Failure is { } failure) { Fail(new InvalidOperationException(failure)); return; }
        var handle = new WindowInteropHelper(_window).Handle;
        var foreground = _window.IsVisible && _window.WindowState != WindowState.Minimized
            && ChildSessionNativeMethods.GetForegroundWindow() == handle && !_settings.Current.Session.WatchMode;
        _session?.SetForegroundLease(foreground);
        if (!foreground || !Ready) { Release(); return; }
        UpdateCapture();
    }
    private void UpdateCapture()
    {
        var alt = RawMouseNative.GetAsyncKeyState(0x12) < 0;
        _gesture.ObserveModifiers(alt, RawMouseNative.GetAsyncKeyState(0x11) < 0);
        var inputWindow = ChildSessionNativeMethods.GetRdpInputWindow(_window.RdpHost.Handle);
        if (inputWindow == 0) { Release(); return; }
        _capture.Observe(inputWindow);
        WindowNativeMethods.TryGetWindowRect(_window.RdpHost.Handle, out var bounds);
        var point = DiagnosticInput.Cursor();
        var inside = point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
        var capture = !alt && !_gesture.Released && Status?.CursorVisible == false && inside
            && ChildSessionNativeMethods.IsRdpInputWindowFocused(_window.RdpHost.Handle);
        if (!capture) { ReleasePointer(); return; }
        _capture.Start(inputWindow, bounds);
        _capture.Constrain(bounds);
    }
    private async Task StartSessionAsync(uint id)
    {
        using var start = new CancellationTokenSource();
        _starting = start;
        var session = new RelativeInputSession();
        _session = session;
        _sessionId = id;
        try
        {
            await session.StartAsync(id, start.Token);
            if (!start.IsCancellationRequested && _window.DataContext is MainWindowViewModel vm)
                vm.NotifyRelativeInputReady();
        }
        catch (OperationCanceledException) when (start.IsCancellationRequested) { }
        catch (Exception error) when (error is System.IO.IOException or Win32Exception or InvalidOperationException
            or OperationCanceledException or UnauthorizedAccessException) { if (!start.IsCancellationRequested) Fail(error); }
        finally
        {
            if (ReferenceEquals(_starting, start)) _starting = null;
            if (start.IsCancellationRequested || !session.Ready) session.Dispose();
        }
    }
    private void Fail(Exception error)
    {
        Failure = error.GetBaseException().Message;
        _failed = true;
        StopSession();
        AppLog.Warn("会话内游戏鼠标不可用。", error);
        if (_window.DataContext is MainWindowViewModel vm) vm.NotifyRelativeInputFailure(Failure);
    }
    private void ReleasePointer()
    {
        if (Captured) Send(new InputPacket(InputOperation.ReleasePointer));
        _capture.Stop();
    }
    private void Release() { ReleasePointer(); _gesture.ClearModifiers(); _session?.SetForegroundLease(false); }
    private void StopSession()
    {
        Release();
        _capture.Detach();
        _starting?.Cancel();
        _session?.Dispose();
        _session = null;
        _sessionId = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _poll.Tick -= Poll;
        _window.RdpHost.LoginCompleted -= OnLogin;
        _window.Deactivated -= OnDeactivated;
        _window.IsVisibleChanged -= OnVisibility;
        StopSession();
        _capture.Dispose();
    }
}
