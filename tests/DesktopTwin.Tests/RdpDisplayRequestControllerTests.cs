using System.Drawing;
using System.Runtime.InteropServices;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpDisplayRequestControllerTests
{
    private static readonly RdpDisplaySettings Requested = new(new Size(1280, 720), 100);

    [Fact]
    public void RejectedRequestCanRetryTheSameResolution()
    {
        var calls = 0;
        var controller = new RdpDisplayRequestController(_ =>
        {
            if (++calls == 1) throw new COMException("synthetic display rejection");
        }, new ManualSessionTime());
        Assert.Throws<COMException>(() => controller.Request(Requested, null));
        Assert.True(controller.Request(Requested, null));
        Assert.False(controller.Request(Requested, null));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FailedChangeCannotReuseThePreviousCachedSuccess()
    {
        var calls = 0;
        var controller = new RdpDisplayRequestController(value =>
        {
            calls++;
            if (value.ScaleFactor == 150) throw new COMException("synthetic display rejection");
        }, new ManualSessionTime());
        Assert.True(controller.Request(Requested, Requested.DesktopSize));
        Assert.Throws<COMException>(() => controller.Request(Requested with { ScaleFactor = 150 }, Requested.DesktopSize));
        Assert.True(controller.Request(Requested, Requested.DesktopSize));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void UnconfirmedRequestCoalescesOnlyUntilItsDeadline()
    {
        var time = new ManualSessionTime();
        var calls = new List<RdpDisplaySettings>();
        var controller = new RdpDisplayRequestController(calls.Add, time);
        Assert.True(controller.Request(Requested, new Size(1920, 1080)));
        time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(controller.Request(Requested, null));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(controller.Request(Requested, new Size(1920, 1080)));
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void ConfirmedSizeCoalescesUntilTheDesktopChangesOrTheConnectionResets()
    {
        var time = new ManualSessionTime();
        var calls = new List<RdpDisplaySettings>();
        var controller = new RdpDisplayRequestController(calls.Add, time);
        Assert.True(controller.Request(Requested, null));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(controller.Request(Requested, Requested.DesktopSize));
        Assert.True(controller.Request(Requested, new Size(1920, 1080)));
        controller.Reset();
        Assert.True(controller.Request(Requested, Requested.DesktopSize));
        Assert.True(controller.Request(Requested with { ScaleFactor = 150 }, Requested.DesktopSize));
        Assert.Equal(4, calls.Count);
    }
}
