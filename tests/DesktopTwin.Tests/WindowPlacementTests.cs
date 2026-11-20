using System.Drawing;
using DesktopTwin.View.Behaviors;

namespace DesktopTwin.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public void SavedNegativeTopIsRestoredWithTheWholeWindowInsideTheWorkArea()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        var result = WindowPlacement.Constrain(new Rectangle(581, -59, 1280, 752), [area], true);
        Assert.Equal(new Rectangle(581, 0, 1280, 752), result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void DraggingAboveTheScreenKeepsTheTitleBarReachable(double dpi)
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        var result = WindowPlacement.Constrain(new Rectangle(300, -200, 750, 500), [area], false, dpi);
        Assert.Equal(0, result.Top);
        Assert.Equal(new Size(750, 500), result.Size);
    }

    [Fact]
    public void ValidNegativeCoordinatesOnAnUpperMonitorArePreserved()
    {
        Rectangle[] areas = [new(0, 0, 1920, 1040), new(0, -1080, 1920, 1040)];
        var bounds = new Rectangle(100, -1000, 1000, 700);
        Assert.Equal(bounds, WindowPlacement.Constrain(bounds, areas, false, 1.5));
    }

    [Fact]
    public void RemovedMonitorAndOversizedHighDpiWindowAreRecovered()
    {
        var area = new Rectangle(-1280, 100, 1280, 680);
        var result = WindowPlacement.Constrain(new Rectangle(3000, -500, 2560, 1504), [area], true, 2);
        Assert.Equal(area, result);
    }

    [Fact]
    public void SnapRectangleIsNotResizedWhenItsTitleBarIsReachable()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        var bounds = new Rectangle(960, 0, 960, 1040);
        Assert.Equal(bounds, WindowPlacement.Constrain(bounds, [area], false));
    }
}
