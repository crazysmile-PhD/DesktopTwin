using System;
using Size = System.Drawing.Size;
using System.Windows;
using DesktopTwin.Contracts;
using DesktopTwin.Interop;
using DesktopTwin.Models;

namespace DesktopTwin.View;

/// <summary>Adapts WPF presentation to the session port and owns the view's native control.</summary>
internal sealed class WpfDesktopSessionView : IDesktopSessionView
{
    private readonly MainWindow _window;

    internal WpfDesktopSessionView(MainWindow window)
    {
        _window = window;
        RdpHost = new RdpSessionClient(window.RdpHost);
        window.IsVisibleChanged += OnVisibilityChanged;
    }

    public bool IsVisible => _window.IsVisible;
    public IRdpSessionClient RdpHost { get; }
    public event EventHandler? VisibilityChanged;
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        VisibilityChanged?.Invoke(this, EventArgs.Empty);

    public void ShowAndActivate()
    {
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Hide() => _window.Hide();
    public void Close()
    {
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.AllowClose = true;
        _window.Close();
    }
}

/// <summary>Keeps the native ActiveX implementation behind the orchestration contract.</summary>
internal sealed class RdpSessionClient(RdpActiveXHost host) : IRdpSessionClient
{
    public int ConnectedState => host.ConnectedState;
    public bool RelativeMouseModeSettingsAccepted => host.RelativeMouseModeSettingsAccepted;
    public bool RelativeMouseModeConfiguredEnabled => host.RelativeMouseModeConfiguredEnabled;
    public ChildSessionConnectionFailedEventArgs? LastConnectionDiagnostic => host.LastConnectionDiagnostic;
    public event EventHandler? LoginCompleted
    {
        add => host.LoginCompleted += value;
        remove => host.LoginCompleted -= value;
    }
    public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed
    {
        add => host.ConnectionFailed += value;
        remove => host.ConnectionFailed -= value;
    }
    public void SetCredentialProvider(Func<(string UserName, string Domain, string Password)?>? provider) => host.SetCredentialProvider(provider);
    public void SetSmartSizing(bool enabled) => host.SetSmartSizing(enabled);
    public void SetSendSystemShortcutsToRemote(bool enabled) => host.SetSendSystemShortcutsToRemote(enabled);
    public void SetAudioMuted(bool muted) => host.SetAudioMuted(muted);
    public void SetRelativeMouseMode(bool enabled) => host.SetRelativeMouseMode(enabled);
    public void SetInputBlocked(bool enabled) => host.SetInputBlocked(enabled);
    public void ConnectToChildSession(Size size) => host.ConnectToChildSession(size);
    public void ReconnectToChildSession(Size size) => host.ReconnectToChildSession(size);
    public void DisconnectSession() => host.DisconnectSession();
    public void CancelPendingReconnect() => host.CancelPendingReconnect();
    public void SendShowDesktopShortcut() => host.SendShowDesktopShortcut();
    public void SendTaskViewShortcut() => host.SendTaskViewShortcut();
}
