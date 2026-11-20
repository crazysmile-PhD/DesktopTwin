using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpCaptureGestureTests
{
    [Fact]
    public void NativeObserverKeepsReceivingReleasedClicksAndDetachesFromReplacedWindows()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckObserver(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native gesture observation did not finish.");
        failure?.Throw();
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
                capture.Observe(first.Handle);
                capture.Stop();
                SendMessage(first.Handle, 0x201, 1, 0);
                SendMessage(first.Handle, 0x202, 0, 0);
                Assert.False(capture.Active);
                Assert.Equal(new[] { 0x201 }, observed);
                capture.Observe(second.Handle);
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
