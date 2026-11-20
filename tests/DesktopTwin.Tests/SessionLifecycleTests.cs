using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using DesktopTwin.Models;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionLifecycleTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public Task OldInitialRetryCannotClearReplacementAttempt() => RunOnStaAsync(async () =>
    {
        // Never construct or dispose an RDP window: Dispose logs off a real user session.
        using var settings = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var sessionCancellation = new CancellationTokenSource();
        var service = (ChildSessionService)RuntimeHelpers.GetUninitializedObject(typeof(ChildSessionService));
        Set(service, "_settings", settings);
        Set(service, "_environmentGate", new object());
        Set(service, "_sessionCancellation", sessionCancellation);
        Set(service, "_initialConnectionRetriesRemaining", 1);
        var first = new TaskCompletionSource<bool>();
        Set(service, "_connectionAttemptCompletionSource", first);
        Invoke(service, "RetryInitialConnectionAsync", new ChildSessionConnectionFailedEventArgs("test", 0));
        first.SetResult(false);
        Set(service, "_startGeneration", 1);
        var replacement = new TaskCompletionSource<bool>();
        Set(service, "_connectionAttemptCompletionSource", replacement);
        Set(service, "_connectionRetryInProgress", true);
        await Task.Delay(4500);
        Assert.True((bool)Get(service, "_connectionRetryInProgress"));
        Assert.False(replacement.Task.IsCompleted);
    });

    [Fact]
    public void ExpiredStartCannotJoinNewGeneration()
    {
        var service = (ChildSessionService)RuntimeHelpers.GetUninitializedObject(typeof(ChildSessionService));
        Set(service, "_environmentGate", new object());
        Set(service, "_startOperationGate", new object());
        Set(service, "_startGeneration", 2);
        Set(service, "_startOperationGeneration", 2);
        var running = new TaskCompletionSource();
        Set(service, "_startOperation", running.Task);
        var rejected = (Task)Invoke(service, "StartSharedAsync", (int?)1);
        Assert.True(rejected.IsCompletedSuccessfully);
        Assert.False(running.Task.IsCompleted);
    }

    [Fact]
    public async Task SwitchingToHostInvalidatesPendingStartupBeforeAnyWindowExists()
    {
        var service = (ChildSessionService)RuntimeHelpers.GetUninitializedObject(typeof(ChildSessionService));
        Set(service, "_environmentGate", new object());
        Set(service, "_autoLaunchPending", true);
        Set(service, "_initialConnectionRetriesRemaining", 1);
        var connection = new TaskCompletionSource<bool>();
        Set(service, "_connectionAttemptCompletionSource", connection);
        Assert.False((bool)Invoke(service, "DisconnectDesktopForSwitchCore"));
        Assert.Equal(1, service.StartGeneration);
        Assert.True(connection.Task.IsCompletedSuccessfully);
        Assert.False(await connection.Task);
        Assert.False((bool)Get(service, "_autoLaunchPending"));
        Assert.Equal(0, (int)Get(service, "_initialConnectionRetriesRemaining"));
    }

    [Fact]
    public void PendingSettingsForNextConnectionAreNotAnActiveReconnect()
    {
        var service = (ChildSessionService)RuntimeHelpers.GetUninitializedObject(typeof(ChildSessionService));
        var pendingField = Field(service, "_pendingRdpSettingChanges");
        pendingField.SetValue(service, Enum.ToObject(pendingField.FieldType, 2));
        Assert.False((bool)Invoke(service, "IsConnectionAttemptInProgress"));
        Set(service, "_rdpSettingsReconnectActive", true);
        Assert.True((bool)Invoke(service, "IsConnectionAttemptInProgress"));
    }

    private static FieldInfo Field(object target, string name) =>
        target.GetType().GetField(name, Private) ?? throw new MissingFieldException(name);
    private static object Get(object target, string name) => Field(target, name).GetValue(target)
        ?? throw new InvalidOperationException(name);
    private static void Set(object target, string name, object value) => Field(target, name).SetValue(target, value);
    private static object Invoke(object target, string name, params object?[] args) =>
        (target.GetType().GetMethod(name, Private) ?? throw new MissingMethodException(name)).Invoke(target, args)
        ?? Task.CompletedTask;

    internal static Task RunOnStaAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
