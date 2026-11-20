using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpCaptureGestureTests
{
    [Fact]
    public void NativeObserverKeepsReceivingReleasedClicksAndDetachesFromReplacedWindows() => RunOnSta(CheckObserver);

    [Fact]
    public void MessageFilterObservesNestedRdpMouseWindowsBeforeNativePreprocessing() => RunOnSta(CheckNestedFilter);

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native gesture observation did not finish.");
        failure?.Throw();
    }

    private static void CheckNestedFilter()
    {
        var root = new System.Windows.Forms.NativeWindow();
        var child = new System.Windows.Forms.NativeWindow();
        root.CreateHandle(new System.Windows.Forms.CreateParams { Parent = (nint)(-3) });
        try
        {
            child.CreateHandle(new System.Windows.Forms.CreateParams { Parent = root.Handle, Style = 0x40000000 });
            try
            {
                var clicks = 0;
                using var capture = new RelativeMouseCapture(_ => false, (_, _, _) => clicks++);
                capture.Observe(root.Handle, root.Handle);
                var message = System.Windows.Forms.Message.Create(child.Handle, 0x201, 1, 0);
                Assert.False(System.Windows.Forms.Application.FilterMessage(ref message));
                Assert.Equal(1, clicks);
                Assert.True(capture.HasLegacyButtons);
                var rightDown = System.Windows.Forms.Message.Create(child.Handle, 0x204, 2, 0);
                System.Windows.Forms.Application.FilterMessage(ref rightDown);
                var leftUp = System.Windows.Forms.Message.Create(child.Handle, 0x202, 0, 0);
                System.Windows.Forms.Application.FilterMessage(ref leftUp);
                Assert.True(capture.HasLegacyButtons); // Releasing one button cannot transfer the other button's ownership.
                var rightUp = System.Windows.Forms.Message.Create(child.Handle, 0x205, 0, 0);
                System.Windows.Forms.Application.FilterMessage(ref rightUp);
                Assert.False(capture.HasLegacyButtons);
                var outside = System.Windows.Forms.Message.Create(0, 0x201, 1, 0);
                Assert.False(System.Windows.Forms.Application.FilterMessage(ref outside));
                Assert.Equal(1, clicks);
                capture.Stop();
                Assert.False(System.Windows.Forms.Application.FilterMessage(ref message));
                Assert.Equal(2, clicks);
                Assert.True(capture.HasLegacyButtons);
                capture.Detach();
                Assert.False(capture.HasLegacyButtons);
                Assert.False(System.Windows.Forms.Application.FilterMessage(ref message));
                Assert.Equal(2, clicks);
            }
            finally { child.DestroyHandle(); }
        }
        finally { root.DestroyHandle(); }
    }

    private static void CheckObserver()
    {
        // Message-only windows: never focus, capture or inject into the user's desktop.
        var first = new System.Windows.Forms.NativeWindow();
        var second = new System.Windows.Forms.NativeWindow();
        first.CreateHandle(new System.Windows.Forms.CreateParams { Parent = (nint)(-3) });
        try
        {
            second.CreateHandle(new System.Windows.Forms.CreateParams { Parent = (nint)(-3) });
            try
            {
                var observed = new List<int>();
                using var capture = new RelativeMouseCapture(_ => throw new InvalidOperationException("Unexpected injection"),
                    (message, _, _) => observed.Add(message));
                capture.Observe(first.Handle, first.Handle);
                capture.Stop();
                SendMessage(first.Handle, 0x201, 1, 0);
                SendMessage(first.Handle, 0x202, 0, 0);
                Assert.False(capture.Active);
                Assert.Equal(new[] { 0x201 }, observed);
                capture.Observe(second.Handle, second.Handle);
                SendMessage(first.Handle, 0x201, 1, 0);
                SendMessage(second.Handle, 0x104, 0x12, 0);
                Assert.Equal(new[] { 0x201, 0x104 }, observed);
                capture.Detach();
                SendMessage(second.Handle, 0x201, 1, 0);
                Assert.Equal(2, observed.Count);
            }
            finally { second.DestroyHandle(); }
        }
        finally { first.DestroyHandle(); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
