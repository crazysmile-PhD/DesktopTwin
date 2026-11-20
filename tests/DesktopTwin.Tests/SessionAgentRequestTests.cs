using System.IO;
using System.Reflection;
using System.Windows.Threading;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionAgentRequestTests
{
    [Fact]
    public Task FileNotificationReceivesHotkeyWithoutPolling() => SessionLifecycleTests.RunOnStaAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopTwin-agent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var service = new SessionAgentService(Dispatcher.CurrentDispatcher, directory);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            service.SwitchRequested += (_, _) => { calls++; received.TrySetResult(); };
            var start = typeof(SessionAgentService).GetMethod("StartPolling", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("StartPolling");
            start.Invoke(service, null);
            var field = typeof(SessionAgentService).GetField("_pollTimer", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("_pollTimer");
            var timer = field.GetValue(service) as DispatcherTimer ?? throw new InvalidOperationException("No timer");
            timer.Stop(); // Only the real filesystem notification can now deliver the request.
            await File.WriteAllTextAsync(service.RequestPath, "{\"source\":\"audit\"}");
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.Equal(1, calls);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "uu-switch-request.json"));
            Directory.Delete(directory);
        }
    });
}
