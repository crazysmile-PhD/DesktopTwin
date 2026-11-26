using System.Drawing;
using DesktopTwin.Contracts;
using DesktopTwin.Models;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class DesktopSessionHostTests
{
    [Fact]
    public void ViewIsLazyAndCreatedOnceWithCurrentSettingsAndFreshCredentials()
    {
        var settings = new SessionSettings();
        var view = new FakeView();
        var creates = 0;
        var password = "first";
        using var host = new DesktopSessionHost(() => { creates++; return view; }, () => settings,
            () => ("user", "domain", password));
        Assert.Null(host.Current);
        Assert.Equal(0, creates);
        settings = settings with { SmartSizing = true, AudioMuted = true, RelativeMouseMode = true, SendSystemShortcutsToRemote = false };
        Assert.Same(view, host.GetOrCreate());
        Assert.Same(view, host.GetOrCreate());
        Assert.Equal(1, creates);
        view.RaiseEvents(new ChildSessionConnectionFailedEventArgs("unobserved", 0));
        Assert.True(view.Client.SmartSizing);
        Assert.True(view.Client.AudioMuted);
        Assert.False(view.Client.RemoteShortcuts);
        Assert.Equal("first", view.Client.CredentialProvider!()!.Value.Password);
        password = "updated";
        Assert.Equal("updated", view.Client.CredentialProvider()!.Value.Password);
    }

    [Fact]
    public void EventsForwardTheirEvidenceAndAreDetachedBeforeClose()
    {
        var view = new FakeView();
        using var host = new DesktopSessionHost(() => view, () => new SessionSettings(), () => null);
        var visibility = 0;
        var logins = 0;
        var sizes = 0;
        var error = new ChildSessionConnectionFailedEventArgs("failure", 42);
        ChildSessionConnectionFailedEventArgs? received = null;
        host.VisibilityChanged += (_, _) => visibility++;
        host.LoginCompleted += (_, _) => logins++;
        host.DesktopSizeChanged += (_, _) => sizes++;
        host.ConnectionFailed += (_, value) => received = value;
        host.GetOrCreate();
        view.RaiseEvents(error);
        Assert.Equal(1, visibility);
        Assert.Equal(1, logins);
        Assert.Equal(1, sizes);
        Assert.Same(error, received);
        received = null;
        host.Dispose();
        host.Dispose();
        Assert.Equal(1, view.CloseCalls);
        Assert.Null(host.Current);
        Assert.Equal(1, visibility);
        Assert.Equal(1, logins);
        Assert.Null(received);
        Assert.Equal(1, sizes);
    }

    [Fact]
    public void DisposingUnusedHostDoesNotCreateAViewAndPreventsLaterCreation()
    {
        using var host = new DesktopSessionHost(() => throw new InvalidOperationException("must not create"),
            () => new SessionSettings(), () => null);
        host.Dispose();
        Assert.Throws<ObjectDisposedException>(host.GetOrCreate);
    }

    [Fact]
    public void ConfigurationFailureClosesPartialViewAndAllowsCleanRetry()
    {
        var failure = new InvalidOperationException("configuration");
        var first = new FakeView();
        first.Client.ConfigurationFailure = failure;
        var second = new FakeView();
        var views = new Queue<FakeView>([first, second]);
        using var host = new DesktopSessionHost(() => views.Dequeue(), () => new SessionSettings(), () => null);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(host.GetOrCreate));
        Assert.Null(host.Current);
        Assert.Equal(1, first.CloseCalls);
        Assert.Same(second, host.GetOrCreate());
    }

    [Fact]
    public void FailedCleanupRetainsBothCauses()
    {
        var configuration = new InvalidOperationException("configuration");
        var cleanup = new InvalidOperationException("close");
        var view = new FakeView { CloseFailure = cleanup };
        view.Client.ConfigurationFailure = configuration;
        using var host = new DesktopSessionHost(() => view, () => new SessionSettings(), () => null);
        var error = Assert.Throws<InvalidOperationException>(host.GetOrCreate);
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(new[] { configuration, cleanup }, aggregate.InnerExceptions);
        Assert.Null(host.Current);
        Assert.Equal(1, view.CloseCalls);
    }

    private sealed class FakeView : IDesktopSessionView
    {
        internal FakeClient Client { get; } = new();
        internal int CloseCalls { get; private set; }
        internal Exception? CloseFailure { get; init; }
        public bool IsVisible => false;
        public Size? AvailableDesktopSize => null;
        public Size? ReportedDesktopSize => null;
        public IRdpSessionClient RdpHost => Client;
        public event EventHandler? VisibilityChanged;
        public event EventHandler? DesktopSizeChanged;
        public void ShowAndActivate() { }
        public void Hide() { }
        public void Close()
        {
            CloseCalls++;
            RaiseEvents(new ChildSessionConnectionFailedEventArgs("closing", 0));
            if (CloseFailure is not null) throw CloseFailure;
        }
        internal void RaiseEvents(ChildSessionConnectionFailedEventArgs error)
        {
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
            DesktopSizeChanged?.Invoke(this, EventArgs.Empty);
            Client.RaiseEvents(error);
        }
    }

    private sealed class FakeClient : IRdpSessionClient
    {
        internal Exception? ConfigurationFailure { get; set; }
        internal bool SmartSizing { get; private set; }
        internal bool AudioMuted { get; private set; }
        internal bool RemoteShortcuts { get; private set; }
        internal Func<(string UserName, string Domain, string Password)?>? CredentialProvider { get; private set; }
        public int ConnectedState => 0;
        public ChildSessionConnectionFailedEventArgs? LastConnectionDiagnostic => null;
        public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;
        public event EventHandler? LoginCompleted;
        public void SetCredentialProvider(Func<(string UserName, string Domain, string Password)?>? provider) => CredentialProvider = provider;
        public void SetSmartSizing(bool enabled)
        {
            if (ConfigurationFailure is not null) throw ConfigurationFailure;
            SmartSizing = enabled;
        }
        public void SetSendSystemShortcutsToRemote(bool enabled) => RemoteShortcuts = enabled;
        public void SetAudioMuted(bool enabled) => AudioMuted = enabled;
        public void SetInputBlocked(bool enabled) { }
        public void ConnectToChildSession(Size size) { }
        public void DisconnectSession() { }
        public void SendShowDesktopShortcut() { }
        public void SendTaskViewShortcut() { }
        internal void RaiseEvents(ChildSessionConnectionFailedEventArgs error)
        {
            LoginCompleted?.Invoke(this, EventArgs.Empty);
            ConnectionFailed?.Invoke(this, error);
        }
    }
}
