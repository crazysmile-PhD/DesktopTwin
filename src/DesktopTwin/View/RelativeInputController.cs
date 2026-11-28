using System;
using System.Collections.Generic;
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
    private readonly ChildSessionService _service;
    private readonly RelativeMouseCapture _capture;
    private readonly DispatcherTimer _poll;
    private readonly RelativeInputActivity _activity;
    private readonly HashSet<ushort> _injectedKeys = [];
    private RelativeInputSession? _session;
    private CancellationTokenSource? _starting;
    private uint? _sessionId;
    private readonly CaptureReleaseGesture _gesture = new();
    private bool _failed;
    private bool _disposed;
    internal bool Captured => _capture.Active;
    internal bool Ready => _session?.Ready == true;
    internal bool Polling => _poll.IsEnabled;
    internal RelativeInputStatus? Status => _session?.Status;
    internal string? Failure { get; private set; }
    internal long RawMoves => _capture.RawMoves;
    internal long NativeClicks => _capture.ObservedClicks;
    internal long NativeKeys => _capture.ObservedKeys;

    internal RelativeInputController(MainWindow window, SettingsService settings, ChildSessionService service)
    {
        _window = window;
        _settings = settings;
        _service = service;
        _capture = new RelativeMouseCapture(Send, ObserveGesture);
        _activity = new RelativeInputActivity(allowed => _session?.SetForegroundLease(allowed),
            () => _session?.RequestStatus(), TimeProvider.System);
        _poll = new DispatcherTimer(DispatcherPriority.Input) { Interval = RelativeInputActivity.DiscoveryInterval };
        _poll.Tick += Poll;
        window.RdpHost.LoginCompleted += OnLogin;
        service.StateChanged += OnActivityChanged;
        window.Activated += OnActivityChanged;
        window.Deactivated += OnActivityChanged;
        window.StateChanged += OnActivityChanged;
        window.IsVisibleChanged += OnVisibility;
    }
    private void OnLogin(object? sender, EventArgs e) { _failed = false; Failure = null; StopSession(); Synchronize(); }
    private void OnActivityChanged(object? sender, EventArgs e) => Synchronize();
    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs e) => Synchronize();
    private void OnStatusRefreshCompleted(object? sender, EventArgs e)
    {
        if (_window.Dispatcher.HasShutdownStarted) return;
        _ = _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(sender, _session)) Synchronize();
        }));
    }
    private bool Send(InputPacket packet) => _session?.TrySend(packet) == true;
    internal bool SendKey(ushort scan, bool up)
    {
        if (!Ready) return false;
        if (!up) { _injectedKeys.Add(scan); Synchronize(); }
        var sent = _activity.Authorized && Send(new InputPacket(InputOperation.Key, scan, up ? 0 : 1));
        if (up || !sent) _injectedKeys.Remove(scan);
        // The active timer handles revocation after the queued key-up; focus loss still revokes immediately.
        return sent;
    }
    internal bool Move(int x, int y) => Captured && Send(new InputPacket(InputOperation.Move, x, y));
    internal bool Click() => Captured && Send(new InputPacket(InputOperation.Button, 1, 1))
        && Send(new InputPacket(InputOperation.Button, 1, 0));

    private void ObserveGesture(int message, int key, bool alt)
    {
        _gesture.ObserveMessage(message, key, alt || Status?.CursorVisible == true);
        if (_gesture.Released) ReleasePointer();
    }

    private void Poll(object? sender, EventArgs e) => Synchronize();
    private void Synchronize()
    {
        if (_disposed) return;
        try { Update(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or System.IO.IOException)
        { Fail(error); }
    }
    private void Update()
    {
        var enabled = _settings.Current.Session.RelativeMouseMode;
        if (!enabled) { _failed = false; Failure = null; StopSession(); return; }
        // The service already samples Windows session identity. Capture polling must not
        // repeat WTS queries, and is needed only while the foreground receiver is ready.
        var id = _service.ChildSessionId;
        if (!_window.RdpHost.LoginComplete || _service.ConnectedState != 1 || id is null)
        { StopSession(); return; }
        if (_sessionId != id && (_session is not null || _starting is not null)) StopSession();
        var session = _session;
        if (session is null)
        {
            _poll.Stop();
            if (_starting is null && !_failed) _ = StartSessionAsync(id.Value);
            return;
        }
        if (session.Failure is { } failure) { Fail(new InvalidOperationException(failure)); return; }
        var handle = new WindowInteropHelper(_window).Handle;
        var foreground = _window.IsVisible && _window.WindowState != WindowState.Minimized
            && ChildSessionNativeMethods.GetForegroundWindow() == handle && !_settings.Current.Session.WatchMode;
        if (!foreground || !session.Ready) { _poll.Stop(); Release(); return; }
        UpdateCapture();
        _activity.Update(true, Captured, _injectedKeys.Count != 0);
        if (_activity.PollInterval is { } interval && _poll.Interval != interval) _poll.Interval = interval;
        _poll.Start();
    }
    private void UpdateCapture()
    {
        var alt = RawMouseNative.GetAsyncKeyState(0x12) < 0;
        _gesture.ObserveModifiers(alt, RawMouseNative.GetAsyncKeyState(0x11) < 0);
        var inputWindow = ChildSessionNativeMethods.GetRdpInputWindow(_window.RdpHost.Handle);
        if (inputWindow == 0) { Release(); return; }
        _capture.Observe(inputWindow, _window.RdpHost.Handle);
        WindowNativeMethods.TryGetWindowRect(_window.RdpHost.Handle, out var bounds);
        var point = DiagnosticInput.Cursor();
        var inside = point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
        var capture = !alt && !_gesture.Released && Status?.CursorVisible == false && inside
            && (Captured || !_capture.HasLegacyButtons)
            && ChildSessionNativeMethods.IsRdpInputWindowFocused(_window.RdpHost.Handle);
        if (!capture) { ReleasePointer(); return; }
        _capture.Start(inputWindow, bounds);
        _capture.Constrain(bounds);
    }
    private async Task StartSessionAsync(uint id)
    {
        using var start = new CancellationTokenSource();
        _starting = start;
        _sessionId = id;
        RelativeInputSession? session = null;
        try
        {
            session = await RelativeInputSession.ConnectAsync(id, start.Token);
            start.Token.ThrowIfCancellationRequested();
            _session = session;
            session.StatusRefreshCompleted += OnStatusRefreshCompleted;
            _sessionId = id;
            Failure = null;
            if (!start.IsCancellationRequested && _window.DataContext is MainWindowViewModel vm)
                vm.NotifyRelativeInputReady();
        }
        catch (OperationCanceledException) when (start.IsCancellationRequested) { }
        catch (Exception error) when (error is System.IO.IOException or Win32Exception or InvalidOperationException
            or OperationCanceledException or UnauthorizedAccessException) { if (!start.IsCancellationRequested) Fail(error); }
        finally
        {
            if (session is not null && (start.IsCancellationRequested || !session.Ready)) session.Dispose();
            if (ReferenceEquals(_starting, start)) { _starting = null; Synchronize(); }
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
    private void Release()
    {
        ReleasePointer();
        _capture.ClearLegacyButtons();
        _gesture.ClearModifiers();
        _injectedKeys.Clear();
        _activity.Update(false, false, false);
    }
    private void StopSession()
    {
        _poll.Stop();
        Release();
        _capture.Detach();
        _starting?.Cancel();
        if (_session is { } session) session.StatusRefreshCompleted -= OnStatusRefreshCompleted;
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
        _service.StateChanged -= OnActivityChanged;
        _window.Activated -= OnActivityChanged;
        _window.Deactivated -= OnActivityChanged;
        _window.StateChanged -= OnActivityChanged;
        _window.IsVisibleChanged -= OnVisibility;
        StopSession();
        _capture.Dispose();
    }
}
