using System.Runtime.InteropServices;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpLocalGraphicsOptionsTests
{
    [Fact]
    public void RequestsBothIndependentOptions()
    {
        var writes = new Dictionary<string, bool>();
        var accepted = RdpLocalGraphicsOptions.Apply((name, value) => writes.Add(name, value));
        Assert.True(writes["EnableFrameBufferRedirection"]);
        Assert.True(writes["EnableHardwareMode"]);
        Assert.True(accepted.FrameBuffer);
        Assert.True(accepted.HardwareDecoding);
    }

    [Fact]
    public void UnsupportedFrameBufferDoesNotSuppressHardwareRequestOrConnection()
    {
        var attempted = new List<string>();
        var accepted = RdpLocalGraphicsOptions.Apply((name, _) =>
        {
            attempted.Add(name);
            if (name == "EnableFrameBufferRedirection") throw new COMException("unsupported");
        });
        Assert.Equal(new[] { "EnableFrameBufferRedirection", "EnableHardwareMode" }, attempted);
        Assert.False(accepted.FrameBuffer);
        Assert.True(accepted.HardwareDecoding);
    }

    [Fact]
    public void BothUnsupportedOptionsLeaveOriginalConnectionAvailable()
    {
        var accepted = RdpLocalGraphicsOptions.Apply((_, _) => throw new COMException("unsupported"));
        Assert.False(accepted.FrameBuffer);
        Assert.False(accepted.HardwareDecoding);
    }
}
