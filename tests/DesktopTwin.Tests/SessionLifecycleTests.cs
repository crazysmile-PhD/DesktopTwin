using System.ComponentModel;
using System.Drawing;
using DesktopTwin.Contracts;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionLifecycleTests
{
    [Fact]
    public async Task SettingsWriteFailureDoesNotPreventExplicitProgramLaunch()
    {
        using var rig = new SessionTestRig();
        rig.Platform.SessionId = 17;
        rig.Settings.Update(settings => settings with { Window = settings.Window with { SmallWindowMode = true } });
        var blocked = rig.Settings.SettingsPath + ".tmp";
        System.IO.Directory.CreateDirectory(blocked);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(rig.Settings.Flush);
            await rig.Service.LaunchProgramAsync("synthetic.exe", false);
            Assert.Equal("synthetic.exe", Assert.Single(rig.Platform.Launches).Path);
            Assert.False(System.IO.File.Exists(rig.Settings.SettingsPath));
        }
        finally { System.IO.Directory.Delete(blocked); }
        rig.Settings.Flush();
        Assert.True(System.IO.File.Exists(rig.Settings.SettingsPath));
    }

    [Fact]
    public async Task IdleAndConnectedSessionsDoNotPollAndDisconnectUpdatesImmediately()
    {
        using var rig = new SessionTestRig();
        var queries = rig.Platform.Queries;
        rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(queries, rig.Platform.Queries);
        Assert.Equal(0, rig.Time.TimerCount);
        var start = rig.Service.StartAsync();
        rig.Platform.SessionId = 17;
        rig.Platform.SessionActive = true;
        rig.View.Client.Login();
        await start;
        queries = rig.Platform.Queries;
        rig.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(queries, rig.Platform.Queries);
        Assert.Equal(0, rig.Time.TimerCount);
        rig.Platform.SessionId = null;
        rig.View.Client.ChangeConnectionState(0);
        Assert.Null(rig.Service.ChildSessionId);
        Assert.Equal(0, rig.Service.ConnectedState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SynchronousArgumentAndAccessFailuresDoNotRetry(bool accessDenied)
    {
        using var rig = new SessionTestRig();
        rig.View.Client.ConnectError = accessDenied ? new UnauthorizedAccessException("denied") : new ArgumentException("invalid");
        await rig.Service.StartAsync();
        rig.Advance(TimeSpan.FromMinutes(3));
        Assert.Single(rig.View.Client.Connections);
        Assert.Equal(0, rig.Time.TimerCount);
        Assert.Contains(accessDenied ? "denied" : "invalid", rig.Service.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoLoginChangesDoNotRestartTeardownOrSuppressedRetry(bool retry)
    {
        using var rig = new SessionTestRig();
        rig.AutoLogin.ReadEnabled = () => rig.Settings.Current.Automation.AutoLoginEnabled;
        rig.Service.SetAutoLoginEnabled(true);
        Assert.Equal(0, rig.AutoLogin.Starts);
        var start = rig.Service.StartAsync();
        Assert.Equal(1, rig.AutoLogin.Starts);
        if (retry) rig.View.Client.Fail();
        else
        {
            rig.View.Client.DisconnectImmediately = false;
            rig.Service.SetAudioMuted(true);
        }

        rig.Service.SetAutoLoginEnabled(false);
        rig.Service.SetAutoLoginEnabled(true);
        Assert.Equal(1, rig.AutoLogin.Starts);
        if (retry)
        {
            rig.Advance(TimeSpan.FromSeconds(4));
            rig.Service.SetAutoLoginEnabled(false);
            rig.Service.SetAutoLoginEnabled(true);
            Assert.Equal(1, rig.AutoLogin.Starts);
        }

        rig.View.Client.ConnectedState = 0;
        await rig.Service.LogoffAndHideAsync();
        await start;
        rig.Service.SetAutoLoginEnabled(false);
        rig.Service.SetAutoLoginEnabled(true);
        Assert.Equal(1, rig.AutoLogin.Starts);
        Assert.Equal(0, rig.Time.TimerCount);
    }

    [Fact]
    public async Task ConcurrentStartsSharePreflightAndOneConnection()
    {
        using var rig = new SessionTestRig();
        var first = rig.Service.StartAsync();
        var second = rig.Service.StartAsync();
        Assert.Same(first, second);
        Assert.Equal(1, rig.Platform.CollectionCalls);
        Assert.Single(rig.View.Client.Connections);
        Assert.False(first.IsCompleted);
        rig.Platform.SessionId = 17;
        rig.View.Client.Login();
        await first;
    }

    [Fact]
    public async Task ConnectedActiveSessionDoesNotReconnectOrRepeatPreflight()
    {
        using var rig = new SessionTestRig();
        var start = rig.Service.StartAsync();
        rig.Platform.SessionId = 17;
        rig.Platform.SessionActive = true;
        rig.View.Client.Login();
        await start;
        await rig.Service.StartAsync();
        Assert.Single(rig.View.Client.Connections);
        Assert.Equal(1, rig.Platform.CollectionCalls);
    }

    [Fact]
    public async Task LogoffDuringPreflightCannotEnableOrReconnectTheOldSession()
    {
        using var rig = new SessionTestRig();
        var preflight = new TaskCompletionSource<SessionEnvironment>();
        rig.Platform.Collection = preflight.Task;
        var start = rig.Service.StartAsync();
        var oldGeneration = rig.Service.StartGeneration;
        await rig.Service.LogoffAndHideAsync();
        preflight.SetResult(SessionEnvironment.Empty);
        await start;
        await rig.Service.StartAsync(oldGeneration);
        Assert.Equal(oldGeneration + 1, rig.Service.StartGeneration);
        Assert.Equal(0, rig.Platform.EnableCalls);
        Assert.Empty(rig.View.Client.Connections);
    }

    [Fact]
    public async Task ExpiredStartCannotJoinNewGeneration()
    {
        using var rig = new SessionTestRig();
        var generation = rig.Service.StartGeneration;
        var old = rig.Service.StartAsync();
        await rig.Service.LogoffAndHideAsync();
        await old;
        var replacement = rig.Service.StartAsync();
        var expired = rig.Service.StartAsync(generation);
        Assert.True(expired.IsCompletedSuccessfully);
        Assert.False(replacement.IsCompleted);
        rig.View.Client.Login();
        await replacement;
        Assert.Equal(2, rig.View.Client.Connections.Count);
    }

    [Fact]
    public async Task OldInitialRetryCannotClearReplacementAttempt()
    {
        using var rig = new SessionTestRig();
        var first = rig.Service.StartAsync();
        rig.View.Client.Fail();
        // A fired callback is already queued on the UI thread when logoff cancels it.
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        await rig.Service.LogoffAndHideAsync();
        await first;
        var replacement = rig.Service.StartAsync();
        rig.Dispatcher.Drain();
        Assert.False(replacement.IsCompleted);
        Assert.Equal(2, rig.View.Client.Connections.Count);
        rig.View.Client.Login();
        await replacement;
    }

    [Fact]
    public async Task InitialRetryGetsAFreshDeadlineAndDoesNotRestartCredentialWatcher()
    {
        using var rig = new SessionTestRig();
        var failures = 0;
        rig.Service.ConnectionFailed += (_, _) => failures++;
        var start = rig.Service.StartAsync();
        rig.Advance(TimeSpan.FromSeconds(59));
        Assert.True(rig.AutoLogin.Starts > 0);
        rig.View.Client.Fail();
        var watcherStarts = rig.AutoLogin.Starts;
        rig.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(2, rig.View.Client.Connections.Count);
        rig.Advance(TimeSpan.FromSeconds(1));
        Assert.False(start.IsCompleted);
        Assert.Equal(watcherStarts, rig.AutoLogin.Starts);
        Assert.Equal(0, failures);
        rig.View.Client.Login();
        await start;
    }

    [Fact]
    public async Task SettingsCoalesceWhileDisconnectingAndUseTheLatestConfiguration()
    {
        using var rig = new SessionTestRig();
        var start = rig.Service.StartAsync();
        rig.Platform.SessionId = 17;
        rig.View.Client.Login();
        await start;
        rig.View.Client.DisconnectImmediately = false;
        var audioApplied = 0;
        var shortcutsApplied = 0;
        rig.Service.AudioReconnectCompleted += (_, _) => audioApplied++;
        rig.Service.SystemShortcutsReconnectCompleted += (_, _) => shortcutsApplied++;
        Assert.True(rig.Service.SetAudioMuted(true));
        Assert.True(rig.Service.SetSendSystemShortcutsToRemote(false));
        rig.Service.SetDesktopSize(1600, 900);
        Assert.Single(rig.View.Client.Connections);
        Assert.Equal(1, rig.View.Client.DisconnectCalls);
        rig.View.Client.ConnectedState = 0;
        rig.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(new Size(1600, 900), rig.View.Client.Connections[1]);
        Assert.True(rig.View.Client.AudioMuted);
        Assert.False(rig.View.Client.RemoteShortcuts);
        Assert.Equal(0, audioApplied);
        rig.View.Client.Login();
        rig.View.Client.Login();
        Assert.Equal(1, audioApplied);
        Assert.Equal(1, shortcutsApplied);
    }

    [Fact]
    public async Task MissingSettingsReconnectCallbackExpiresOnceAndPreservesPendingSettings()
    {
        using var rig = new SessionTestRig();
        var start = rig.Service.StartAsync();
        rig.Platform.SessionId = 17;
        rig.View.Client.Login();
        await start;
        var failures = 0;
        var applied = 0;
        rig.Service.ConnectionFailed += (_, _) => failures++;
        rig.Service.AudioReconnectCompleted += (_, _) => applied++;
        rig.View.Client.DisconnectImmediately = false;
        rig.Service.SetAudioMuted(true);
        rig.Advance(TimeSpan.FromSeconds(60));
        rig.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(1, failures);
        Assert.Equal(0, applied);
        rig.View.Client.ConnectedState = 0;
        rig.Platform.SessionActive = false;
        var replacement = rig.Service.StartAsync();
        rig.View.Client.Login();
        await replacement;
        Assert.Equal(1, applied);
        Assert.True(rig.Service.AudioMuted);
    }

    [Fact]
    public void PendingSettingsForNextConnectionAreNotAnActiveReconnect()
    {
        using var rig = new SessionTestRig();
        Assert.False(rig.Service.SetAudioMuted(true));
        Assert.Empty(rig.View.Client.Connections);
        rig.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, rig.AutoLogin.Starts);
        Assert.True(rig.Service.AudioMuted);
    }

    [Fact]
    public async Task CredentialWatcherCannotRestartWhileTheControlIsStillDisconnecting()
    {
        using var rig = new SessionTestRig();
        var start = rig.Service.StartAsync();
        rig.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, rig.AutoLogin.Starts);
        rig.View.Client.DisconnectImmediately = false;
        rig.Service.SetAudioMuted(true);
        // A stale native Connected=2 is not evidence of an owned login attempt.
        rig.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, rig.AutoLogin.Starts);
        rig.View.Client.ConnectedState = 0;
        await rig.Service.LogoffAndHideAsync();
        await start;
    }

    [Fact]
    public async Task ExitJoinsTheAcceptedRequestWithoutWaitingForWindowsCleanup()
    {
        using var rig = new SessionTestRig();
        rig.Platform.SessionId = 17;
        var request = new TaskCompletionSource<uint?>();
        rig.Platform.LogoffRequest = request.Task;
        var cleanup = rig.Service.LogoffAndHideAsync();
        var firstExit = rig.Service.RequestLogoffForExitAsync();
        var secondExit = rig.Service.RequestLogoffForExitAsync();
        Assert.False(firstExit.IsCompleted);
        request.SetResult(17);
        await Task.WhenAll(firstExit, secondExit);
        Assert.False(cleanup.IsCompleted);
        Assert.Equal(new uint[] { 17 }, rig.Platform.Logoffs);
        await rig.Service.StartAsync();
        Assert.Empty(rig.View.Client.Connections);
        rig.Service.Dispose();
        Assert.Single(rig.Platform.Logoffs);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup);
    }

    [Fact]
    public async Task ExitDoesNotTreatRejectedLogoffRequestAsAccepted()
    {
        using var rig = new SessionTestRig();
        rig.Platform.SessionId = 17;
        var request = new TaskCompletionSource<uint?>();
        rig.Platform.LogoffRequest = request.Task;
        var exit = rig.Service.RequestLogoffForExitAsync();
        request.SetException(new Win32Exception(5));
        await Assert.ThrowsAsync<Win32Exception>(() => exit);
    }

    [Fact]
    public async Task ExpectedSessionMismatchDoesNotCancelTheCurrentStartOrLogoffAReplacement()
    {
        using var rig = new SessionTestRig();
        var start = rig.Service.StartAsync();
        var generation = rig.Service.StartGeneration;
        rig.Platform.SessionId = 18;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.LogoffAndHideAsync(17));
        Assert.Equal(generation, rig.Service.StartGeneration);
        Assert.Empty(rig.Platform.Logoffs);
        Assert.False(start.IsCompleted);
        rig.View.Client.Login();
        await start;
    }

    [Fact]
    public async Task LogoffCancelsQueuedLaunchWithoutWaitingForTheNativeLaunchToFinish()
    {
        using var rig = new SessionTestRig();
        rig.Platform.SessionId = 17;
        var nativeLaunch = new TaskCompletionSource();
        rig.Platform.Launch = nativeLaunch.Task;
        var first = rig.Service.LaunchProgramAsync("synthetic-first.exe", false);
        var second = rig.Service.LaunchProgramAsync("synthetic-second.exe", false);
        await rig.Service.LogoffAndHideAsync();
        await second;
        Assert.True(rig.Platform.LaunchCancellation.IsCancellationRequested);
        Assert.Single(rig.Platform.Launches);
        nativeLaunch.SetResult();
        await first;
        Assert.Equal(new uint[] { 17 }, rig.Platform.Logoffs);
    }

    [Fact]
    public async Task HiddenWindowStaysHiddenAndStatusQueueIsBoundedDuringBusyUi()
    {
        using var rig = new SessionTestRig();
        rig.Service.ShowWindow();
        rig.Service.HideWindow();
        var start = rig.Service.StartAsync();
        Assert.False(rig.View.IsVisible);
        Assert.Equal(1, rig.View.ShowCalls);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        // The only connection timer is its deadline; idle status reads no longer queue UI work.
        Assert.Equal(0, rig.Dispatcher.QueuedCount);
        rig.Dispatcher.Drain();
        rig.View.Client.Login();
        await start;
        rig.Service.Dispose();
        rig.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, rig.Time.TimerCount);
        Assert.Equal(1, rig.View.CloseCalls);
        Assert.Throws<ObjectDisposedException>(() => rig.Service.ShowWindow());
    }

    [Fact]
    public async Task NewerEnvironmentCheckWinsEvenWhenOlderCollectionFinishesLast()
    {
        using var rig = new SessionTestRig();
        var old = new TaskCompletionSource<SessionEnvironment>();
        var current = new TaskCompletionSource<SessionEnvironment>();
        rig.Platform.Collection = old.Task;
        var first = rig.Service.RefreshEnvironmentCheckAsync();
        rig.Platform.Collection = current.Task;
        var second = rig.Service.RefreshEnvironmentCheckAsync();
        current.SetResult(new SessionEnvironment(true, true, "newer"));
        await second;
        old.SetResult(SessionEnvironment.Empty);
        await first;
        Assert.True(rig.Service.HasBlockingEnvironmentIssue);
        Assert.Contains("newer", rig.Service.EnvironmentIssueSummary);
    }
}
