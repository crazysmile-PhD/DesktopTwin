using System.Drawing;
using DesktopTwin.Models;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionConnectionTests
{
    [Fact]
    public async Task LoginCompletesSharedWaitersAndReleasesTheOnlyDeadlineTimer()
    {
        using var probe = new Probe();
        Assert.False(probe.Connection.CompleteLogin());
        var first = probe.Connection.Start(false);
        Assert.Same(first, probe.Connection.Start(false));
        Assert.Equal(SessionConnectionPhase.Connecting, probe.Connection.Phase);
        Assert.Equal(1, probe.Time.TimerCount);
        probe.Client.Login();
        await first;
        Assert.Equal(SessionConnectionPhase.Idle, probe.Connection.Phase);
        Assert.Equal(0, probe.Time.TimerCount);
        Assert.False(probe.Connection.CompleteLogin());
        probe.Advance(120);
        Assert.Empty(probe.Failures);
    }

    [Fact]
    public async Task InitialFailureRetriesOnceAndDuplicateCallbacksCannotSpendTheBudget()
    {
        using var probe = new Probe();
        var task = probe.Connection.Start(false);
        probe.Client.Fail();
        probe.Client.Fail();
        Assert.True(probe.Connection.SuppressCredentialDialogFill);
        Assert.Single(probe.Retries);
        probe.Advance(3);
        Assert.Single(probe.Client.Connections);
        probe.Advance(1);
        Assert.Equal(2, probe.Client.Connections.Count);
        probe.Client.Fail();
        await task;
        Assert.Single(probe.Failures);
        probe.Client.Fail();
        Assert.Single(probe.Failures);
        Assert.Equal(0, probe.Time.TimerCount);
    }

    [Fact]
    public async Task SettingsRetriesUseTwoFourEightSecondsAndStopAfterThreeRetries()
    {
        using var probe = new Probe();
        var task = probe.Connection.Start(false, true);
        foreach (var seconds in new[] { 2, 4, 8 })
        {
            probe.Client.Fail();
            Assert.False(probe.Connection.SuppressCredentialDialogFill);
            probe.Advance(seconds);
        }
        Assert.Equal(new double[] { 2, 4, 8 }, probe.Retries.Select(retry => retry.TotalSeconds));
        Assert.Equal(4, probe.Client.Connections.Count);
        probe.Client.Fail();
        await task;
        Assert.Single(probe.Failures);
    }

    [Fact]
    public async Task DisconnectAndReleaseAreObservedWithoutAFixedOneSecondDelay()
    {
        using var probe = new Probe();
        probe.Client.ConnectedState = 1;
        probe.Client.DisconnectImmediately = false;
        probe.Active = true;
        var task = probe.Connection.Start(true);
        Assert.Equal(SessionConnectionPhase.Disconnecting, probe.Connection.Phase);
        probe.Client.Fail();
        Assert.Empty(probe.Retries);
        probe.Advance(.2);
        Assert.Empty(probe.Client.Connections);
        probe.Client.ConnectedState = 0;
        probe.Advance(.2);
        Assert.Equal(SessionConnectionPhase.WaitingForRelease, probe.Connection.Phase);
        probe.Client.Fail();
        probe.Advance(.2);
        Assert.Empty(probe.Client.Connections);
        probe.Active = false;
        probe.Size = new Size(1280, 720);
        probe.Advance(.2);
        Assert.Equal(new Size(1280, 720), Assert.Single(probe.Client.Connections));
        Assert.Equal(TimeSpan.FromSeconds(.8).Ticks, probe.Time.GetTimestamp());
        probe.Client.Login();
        await task;
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 10)]
    public async Task MissingDisconnectOrReleaseHasOneBoundedFailure(bool disconnected, int timeout)
    {
        using var probe = new Probe();
        probe.Client.ConnectedState = disconnected ? 0 : 1;
        probe.Client.DisconnectImmediately = disconnected;
        probe.Active = true;
        var task = probe.Connection.Start(true);
        probe.Advance(timeout);
        await task;
        Assert.Equal(1460, Assert.Single(probe.Failures).ErrorCode);
        Assert.Equal((disconnected ? SessionConnectionPhase.WaitingForRelease : SessionConnectionPhase.Disconnecting,
            TimeSpan.FromSeconds(timeout)), Assert.Single(probe.Timeouts));
        Assert.Empty(probe.Client.Connections);
        probe.Advance(120);
        Assert.Single(probe.Failures);
    }

    [Fact]
    public async Task LostLoginCallbackTimesOutAndLateLoginCannotReviveTheOperation()
    {
        using var probe = new Probe();
        var task = probe.Connection.Start(false);
        probe.Advance(60);
        await task;
        Assert.Equal(1460, Assert.Single(probe.Failures).ErrorCode);
        Assert.False(probe.Connection.CompleteLogin());
        Assert.False(probe.Connection.IsActive);
        Assert.Equal(1, probe.Client.DisconnectCalls);
    }

    [Fact]
    public async Task AQueuedOldTimeoutCannotFinishAReplacementOperation()
    {
        using var probe = new Probe();
        var first = probe.Connection.Start(false);
        probe.Time.Advance(TimeSpan.FromSeconds(60));
        probe.Connection.Cancel();
        await first;
        var replacement = probe.Connection.Start(false);
        probe.Dispatcher.Drain();
        Assert.False(replacement.IsCompleted);
        Assert.Empty(probe.Failures);
        probe.Client.Login();
        await replacement;
    }

    [Fact]
    public async Task SettingsDuringBackoffAndReleaseCoalesceIntoOneConnectWithLatestSize()
    {
        using var probe = new Probe();
        var task = probe.Connection.Start(false);
        probe.Client.Fail();
        Assert.Same(task, probe.Connection.Start(true, true));
        probe.Active = true;
        probe.Advance(4);
        Assert.Equal(SessionConnectionPhase.WaitingForRelease, probe.Connection.Phase);
        probe.Size = new Size(1440, 900);
        Assert.Same(task, probe.Connection.Start(true, true));
        probe.Active = false;
        probe.Advance(.2);
        Assert.Equal(2, probe.Client.Connections.Count);
        Assert.Equal(probe.Size, probe.Client.Connections[1]);
        probe.Client.Login();
        await task;
    }

    [Fact]
    public async Task SettingsDuringConnectRestartOnceAndSynchronousLoginOwnsItsTimer()
    {
        using var probe = new Probe();
        var task = probe.Connection.Start(false);
        probe.Client.OnConnect = probe.Client.Login;
        Assert.Same(task, probe.Connection.Start(true, true));
        await task;
        Assert.Equal(2, probe.Client.Connections.Count);
        Assert.Equal(0, probe.Time.TimerCount);
    }

    [Fact]
    public async Task NativeConnectExceptionsUseTheSameBoundedRetryPath()
    {
        using var probe = new Probe();
        probe.Client.ConnectError = new InvalidOperationException("connect rejected");
        var task = probe.Connection.Start(false);
        probe.Advance(4);
        await task;
        Assert.Equal("connect rejected", Assert.Single(probe.Failures).Message);
        Assert.Equal(2, probe.Client.Connections.Count);
    }

    [Fact]
    public async Task NativeDisconnectAndPollingErrorsFinishWaitersEvenIfCleanupAlsoFails()
    {
        using var probe = new Probe();
        probe.Client.DisconnectError = new InvalidOperationException("disconnect rejected");
        await probe.Connection.Start(true);
        Assert.Equal("disconnect rejected", Assert.Single(probe.Failures).Message);
        probe.Client.DisconnectError = null;
        probe.Active = true;
        var next = probe.Connection.Start(true);
        probe.QueryError = new InvalidOperationException("query rejected");
        probe.Advance(.2);
        await next;
        Assert.Equal("query rejected", probe.Failures[1].Message);
    }

    [Fact]
    public async Task UnexpectedDisconnectAfterLoginIsReportedOnceAndDisposalCancelsQueuedWork()
    {
        using var probe = new Probe();
        var task = probe.Connection.Start(false);
        probe.Client.Login();
        await task;
        probe.Client.Fail();
        probe.Client.Fail();
        Assert.Single(probe.Failures);
        var next = probe.Connection.Start(false);
        probe.Client.Fail();
        probe.Time.Advance(TimeSpan.FromSeconds(4));
        probe.Connection.Dispose();
        probe.Connection.Dispose();
        probe.Dispatcher.Drain();
        await next;
        probe.Client.Fail();
        Assert.False(probe.Connection.CompleteLogin());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.Connection.Start(false));
        Assert.Single(probe.Failures);
        Assert.Equal(0, probe.Time.TimerCount);
    }

    private sealed class Probe : IDisposable
    {
        internal ManualSessionTime Time { get; } = new();
        internal TestSessionDispatcher Dispatcher { get; } = new();
        internal FakeRdpClient Client { get; } = new();
        internal SessionConnection Connection { get; }
        internal Size Size { get; set; } = new(1920, 1080);
        internal bool Active { get; set; }
        internal Exception? QueryError { get; set; }
        internal List<ChildSessionConnectionFailedEventArgs> Failures { get; } = [];
        internal List<TimeSpan> Retries { get; } = [];
        internal List<(SessionConnectionPhase Phase, TimeSpan Budget)> Timeouts { get; } = [];
        internal Probe()
        {
            Connection = new SessionConnection(Dispatcher, Time, () => Client, () => Size,
                () => QueryError is { } error ? throw error : Active,
                (phase, budget) =>
                {
                    Timeouts.Add((phase, budget));
                    return new ChildSessionConnectionFailedEventArgs("timeout", 1460);
                });
            Connection.Failed += (_, error) => Failures.Add(error);
            Connection.Retrying += (_, _, delay, _) => Retries.Add(delay);
            Client.ConnectionFailed += (_, error) => Connection.ReportFailure(error);
            Client.LoginCompleted += (_, _) => Connection.CompleteLogin();
        }
        internal void Advance(double seconds) { Time.Advance(TimeSpan.FromSeconds(seconds)); Dispatcher.Drain(); }
        public void Dispose() => Connection.Dispose();
    }
}
