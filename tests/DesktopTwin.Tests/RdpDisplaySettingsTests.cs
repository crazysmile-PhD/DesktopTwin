using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpDisplaySettingsTests
{
    [Theory]
    [InlineData(954, 1005, 1, 954, 1005, 100)]
    [InlineData(954, 650, 1.5, 1431, 975, 150)]
    [InlineData(750, 450, 2, 1500, 900, 200)]
    [InlineData(200, 100, 1, 640, 480, 100)]
    [InlineData(10000, 10000, 2, 7680, 4320, 200)]
    public void RemoteResolutionUsesTheAvailablePhysicalPixelsAndLocalDpi(double width, double height,
        double dpi, int expectedWidth, int expectedHeight, uint expectedScale)
    {
        var settings = RdpDisplaySettings.FromViewport(width, height, dpi);
        Assert.Equal(expectedWidth, settings.DesktopSize.Width);
        Assert.Equal(expectedHeight, settings.DesktopSize.Height);
        Assert.Equal(expectedScale, settings.ScaleFactor);
        Assert.InRange(settings.PhysicalWidth, 10u, 10000u);
        Assert.InRange(settings.PhysicalHeight, 10u, 10000u);
    }
}
