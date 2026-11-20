using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>Drains stdout concurrently with exit under one deadline, including output held open by a child.</summary>
internal static class BoundedProcessRunner
{
    internal static async Task<(string? Output, int ExitCode)> RunAsync(string fileName, string arguments, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            });
            if (process is null) return (null, -1);
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var completed = Task.WhenAll(output, process.WaitForExitAsync(deadline.Token));
            try
            {
                await completed.WaitAsync(deadline.Token).ConfigureAwait(false);
                return (await output.ConfigureAwait(false), process.ExitCode);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                await StopAsync(process).ConfigureAwait(false);
                // A descendant can keep the pipe open after the parent exits. Closing our end bounds that wait too.
                process.StandardOutput.Dispose();
                _ = ObserveAsync(completed);
                return (null, -1);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException)
        {
            AppLog.Warn($"运行 {Path.GetFileName(fileName)} 失败。", error);
            return (null, -1);
        }
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or TimeoutException)
        {
            AppLog.Warn("外部命令超时，未能确认进程树已结束。", error);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Expected when closing the redirected stream at the deadline. Always observe the task.
        }
    }
}
