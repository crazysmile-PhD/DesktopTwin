using System;
using System.Drawing;
using DesktopTwin.Models;

namespace DesktopTwin.Contracts;

/// <summary>UI-thread port for the owned desktop view; no WPF or ActiveX types cross this boundary.</summary>
internal interface IDesktopSessionView
{
    bool IsVisible { get; }
    Size? AvailableDesktopSize { get; }
    Size? ReportedDesktopSize { get; }
    IRdpSessionClient RdpHost { get; }
    event EventHandler? VisibilityChanged;
    event EventHandler? DesktopSizeChanged;
    void ShowAndActivate();
    void Hide();
    void Close();
}

/// <summary>Commands and evidence used by session orchestration, independent of the native control.</summary>
internal interface IRdpSessionClient
{
    int ConnectedState { get; }
    bool RelativeMouseModeSettingsAccepted { get; }
    bool RelativeMouseModeConfiguredEnabled { get; }
    ChildSessionConnectionFailedEventArgs? LastConnectionDiagnostic { get; }
    event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;
    event EventHandler? LoginCompleted;
    void SetCredentialProvider(Func<(string UserName, string Domain, string Password)?>? provider);
    void SetSmartSizing(bool enabled);
    void SetSendSystemShortcutsToRemote(bool enabled);
    void SetAudioMuted(bool muted);
    void SetRelativeMouseMode(bool enabled);
    void SetInputBlocked(bool enabled);
    void ConnectToChildSession(Size size);
    void ReconnectToChildSession(Size size);
    void DisconnectSession();
    void CancelPendingReconnect();
    void SendShowDesktopShortcut();
    void SendTaskViewShortcut();
}
