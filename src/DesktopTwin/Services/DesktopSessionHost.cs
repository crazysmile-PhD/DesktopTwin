using System;
using DesktopTwin.Contracts;
using DesktopTwin.Models;

namespace DesktopTwin.Services;

/// <summary>Owns one lazily created session view, configuration and event subscriptions.</summary>
internal sealed class DesktopSessionHost(
    Func<IDesktopSessionView> createView,
    Func<SessionSettings> readSettings,
    Func<(string UserName, string Domain, string Password)?> credentialProvider) : IDisposable
{
    private bool _disposed;
    internal IDesktopSessionView? Current { get; private set; }
    internal event EventHandler? VisibilityChanged;
    internal event EventHandler? LoginCompleted;
    internal event EventHandler? DesktopSizeChanged;
    internal event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    internal IDesktopSessionView GetOrCreate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is not null) return Current;
        var view = createView();
        try
        {
            var settings = readSettings();
            view.RdpHost.SetCredentialProvider(credentialProvider);
            view.RdpHost.SetSmartSizing(settings.SmartSizing);
            view.RdpHost.SetSendSystemShortcutsToRemote(settings.SendSystemShortcutsToRemote);
            view.RdpHost.SetAudioMuted(settings.AudioMuted);
            view.RdpHost.SetRelativeMouseMode(settings.RelativeMouseMode);
        }
        catch (Exception configurationError)
        {
            try { view.Close(); }
            catch (Exception closeError)
            {
                throw new InvalidOperationException("Failed to configure and close the desktop view.",
                    new AggregateException(configurationError, closeError));
            }
            throw;
        }
        view.VisibilityChanged += OnVisibilityChanged;
        view.DesktopSizeChanged += OnDesktopSizeChanged;
        view.RdpHost.LoginCompleted += OnLoginCompleted;
        view.RdpHost.ConnectionFailed += OnConnectionFailed;
        return Current = view;
    }

    private void OnVisibilityChanged(object? sender, EventArgs e) => VisibilityChanged?.Invoke(this, e);
    private void OnDesktopSizeChanged(object? sender, EventArgs e) => DesktopSizeChanged?.Invoke(this, e);
    private void OnLoginCompleted(object? sender, EventArgs e) => LoginCompleted?.Invoke(this, e);
    private void OnConnectionFailed(object? sender, ChildSessionConnectionFailedEventArgs e) => ConnectionFailed?.Invoke(this, e);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var view = Current;
        Current = null;
        if (view is null) return;
        view.VisibilityChanged -= OnVisibilityChanged;
        view.DesktopSizeChanged -= OnDesktopSizeChanged;
        view.RdpHost.LoginCompleted -= OnLoginCompleted;
        view.RdpHost.ConnectionFailed -= OnConnectionFailed;
        view.Close();
    }
}
