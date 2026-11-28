using DesktopTwin.Input;

namespace DesktopTwin.Tests;

public sealed class RelativeInputActivityTests
{
    [Fact]
    public void ForegroundWithoutCaptureOnlyRequestsCoalescedPassiveDiscovery()
    {
        var time = new ManualSessionTime();
        var authorization = new List<bool>();
        var requests = 0;
        var activity = new RelativeInputActivity(authorization.Add, () => requests++, time);
        for (var i = 0; i < 100; i++) activity.Update(true, false, false);
        Assert.Equal(TimeSpan.FromMilliseconds(250), activity.PollInterval);
        Assert.False(activity.Authorized);
        Assert.DoesNotContain(true, authorization);
        Assert.Equal(1, requests);
        time.Advance(TimeSpan.FromMilliseconds(249));
        activity.Update(true, false, false);
        Assert.Equal(1, requests);
        time.Advance(TimeSpan.FromMilliseconds(1));
        activity.Update(true, false, false);
        Assert.Equal(2, requests);
    }

    [Fact]
    public void CaptureRenewsItsShortLeaseAndReleaseReturnsToPassiveDiscovery()
    {
        var time = new ManualSessionTime();
        var authorization = new List<bool>();
        var requests = 0;
        var activity = new RelativeInputActivity(authorization.Add, () => requests++, time);
        activity.Update(true, true, false);
        time.Advance(TimeSpan.FromMilliseconds(32));
        activity.Update(true, true, false);
        Assert.Equal(new[] { true, true }, authorization);
        Assert.Equal(TimeSpan.FromMilliseconds(32), activity.PollInterval);
        Assert.Equal(0, requests);
        activity.Update(true, false, false);
        Assert.False(authorization[^1]);
        Assert.Equal(TimeSpan.FromMilliseconds(250), activity.PollInterval);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void BackgroundStopsDiscoveryAndRevokesEvenCapturedOrExplicitKeyInput()
    {
        var requests = 0;
        var authorization = new List<bool>();
        var activity = new RelativeInputActivity(authorization.Add, () => requests++, new ManualSessionTime());
        activity.Update(true, false, true);
        Assert.True(activity.Authorized);
        Assert.Equal(TimeSpan.FromMilliseconds(32), activity.PollInterval);
        activity.Update(false, true, true);
        Assert.False(activity.Authorized);
        Assert.Null(activity.PollInterval);
        Assert.Equal(0, requests);
        Assert.Equal(new[] { true, false }, authorization);
        activity.Update(true, false, false);
        Assert.Equal(1, requests);
    }
}
