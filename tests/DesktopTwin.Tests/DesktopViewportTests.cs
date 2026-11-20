using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.View;

namespace DesktopTwin.Tests;

public sealed class DesktopViewportTests
{
    [Theory]
    [InlineData(960, 1000, 16, 9, 960, 540, 0, 230)] // Half-screen game beside a video.
    [InlineData(2560, 1080, 16, 9, 1920, 1080, 320, 0)]
    [InlineData(960, 1000, 16, 10, 960, 600, 0, 200)]
    [InlineData(640, 960, 4, 3, 640, 480, 0, 240)]
    [InlineData(960, 1000, 9, 16, 562.5, 1000, 198.75, 0)]
    [InlineData(1920, 1080, 16, 9, 1920, 1080, 0, 0)]
    [InlineData(0, 1000, 16, 9, 0, 0, 0, 500)]
    [InlineData(960, 0, 16, 9, 0, 0, 480, 0)]
    public void DesktopFitsWithoutStretchingOrCropping(double width, double height,
        double ratioWidth, double ratioHeight, double expectedWidth, double expectedHeight,
        double expectedX, double expectedY) => RunOnSta(() =>
    {
        var child = new Border();
        var viewport = new DesktopViewport { Child = child, AspectRatio = ratioWidth / ratioHeight };
        var size = new Size(width, height);
        viewport.Measure(size);
        viewport.Arrange(new Rect(size));

        AssertClose(expectedWidth, child.ActualWidth);
        AssertClose(expectedHeight, child.ActualHeight);
        var origin = child.TranslatePoint(new Point(), viewport);
        AssertClose(expectedX, origin.X);
        AssertClose(expectedY, origin.Y);
        Assert.Equal(size, viewport.RenderSize);
    });

    [Fact]
    public void ResizeAndResolutionChangesPreserveWindowPlacement() => RunOnSta(() =>
    {
        var child = new Border();
        var viewport = new DesktopViewport { Child = child };
        using var fixture = new WindowFixture(viewport);
        fixture.Window.Width = 600;
        fixture.Window.Height = 700;
        DrainDispatcher();
        var left = fixture.Window.Left;
        var top = fixture.Window.Top;

        AssertClose(600, fixture.Window.ActualWidth);
        AssertClose(700, fixture.Window.ActualHeight);
        AssertClose(600, child.ActualWidth);
        AssertClose(337.5, child.ActualHeight);

        viewport.AspectRatio = 4d / 3d;
        DrainDispatcher();
        AssertClose(600, child.ActualWidth);
        AssertClose(450, child.ActualHeight);
        AssertClose(600, fixture.Window.ActualWidth);
        AssertClose(700, fixture.Window.ActualHeight);
        AssertClose(left, fixture.Window.Left);
        AssertClose(top, fixture.Window.Top);

        viewport.Visibility = Visibility.Collapsed;
        DrainDispatcher();
        viewport.Visibility = Visibility.Visible;
        fixture.Window.Width = 800;
        fixture.Window.Height = 500;
        DrainDispatcher();
        AssertClose(2000d / 3d, child.ActualWidth);
        AssertClose(500, child.ActualHeight);
        AssertClose(800, fixture.Window.ActualWidth);
        AssertClose(500, fixture.Window.ActualHeight);
    });

    [Fact]
    public void NativeRdpControlReceivesTheFittedRectangleWithoutConnecting() => RunOnSta(() =>
    {
        using var rdp = new RdpActiveXHost();
        using var host = new WindowsFormsHost { Child = rdp };
        var viewport = new DesktopViewport { Child = host };
        using var fixture = new WindowFixture(viewport);

        foreach (var size in new[] { new Size(600, 700), new Size(800, 400), new Size(640, 360) })
        {
            fixture.Window.Width = size.Width;
            fixture.Window.Height = size.Height;
            DrainDispatcher();

            Assert.True(WindowNativeMethods.TryGetWindowRect(rdp.Handle, out var bounds));
            var topLeft = host.PointToScreen(new Point());
            var bottomRight = host.PointToScreen(new Point(host.ActualWidth, host.ActualHeight));
            Assert.InRange(Math.Abs(bounds.Left - topLeft.X), 0, 1);
            Assert.InRange(Math.Abs(bounds.Top - topLeft.Y), 0, 1);
            Assert.InRange(Math.Abs(bounds.Right - bottomRight.X), 0, 1);
            Assert.InRange(Math.Abs(bounds.Bottom - bottomRight.Y), 0, 1);
            Assert.InRange(Math.Abs((bounds.Right - bounds.Left) / viewport.AspectRatio
                - (bounds.Bottom - bounds.Top)), 0, 1);
            Assert.Equal(0, rdp.ConnectedState);
        }

        host.Child = null;
    });

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0, 0.01);

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF layout test did not complete.");
        failure?.Throw();
    }

    private sealed class WindowFixture : IDisposable
    {
        internal Window Window { get; }

        internal WindowFixture(UIElement content)
        {
            Window = new Window
            {
                Width = 640, Height = 420, Left = 20, Top = 20,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Content = content
            };
            Window.Show();
            DrainDispatcher();
        }

        public void Dispose() => Window.Close();
    }
}
