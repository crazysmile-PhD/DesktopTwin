using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DesktopTwin.Interop;

namespace DesktopTwin.View;

/// <summary>Opt-in diagnostic focus loss using only an owned, temporary window.</summary>
internal static class DesktopInputFocusProbe
{
    internal static async Task<object> RunAsync(MainWindow window, Func<object> capture, CancellationToken cancellationToken)
    {
        var before = capture();
        var probe = new Window
        {
            Owner = window, Title = "DesktopTwin input focus test", Width = 340, Height = 120,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock { Text = "正在测试失焦释放，完成后自动返回。", Margin = new Thickness(16) }
        };
        object during;
        var restore = false;
        try
        {
            probe.Show();
            probe.Activate();
            await Task.Delay(300, cancellationToken);
            var handle = new WindowInteropHelper(probe).Handle;
            restore = ChildSessionNativeMethods.GetForegroundWindow() == handle;
            if (!restore) throw new InvalidOperationException("The focus probe did not receive foreground focus.");
            during = capture();
        }
        finally
        {
            probe.Close();
            if (restore && window.IsVisible)
            {
                window.Activate();
                ChildSessionNativeMethods.TryFocusRdpInputWindow(window.RdpHost.Handle);
            }
        }
        await Task.Delay(200, cancellationToken);
        return new { before, during, after = capture() };
    }
}
