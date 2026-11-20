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
    public Task ExitJoinsTheAcceptedRequestWithoutWaitingForWindowsCleanup() => RunOnStaAsync(async () =>
    {
        // 已有注销在途，只替换任务信号；绝不创建窗口或调用真实 WTS 注销。
        var service = CreateServiceSkeleton();
        Set(service, "_dispatcher", Dispatcher.CurrentDispatcher);
        Set(service, "_environmentGate", new object());
        Set(service, "_sessionClosing", true);
        var request = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(service, "_logoffRequest", request.Task);
        Set(service, "_logoffOperation", cleanup.Task);
        var firstExit = service.RequestLogoffForExitAsync();
        var secondExit = service.RequestLogoffForExitAsync();
        Assert.False(firstExit.IsCompleted);
        Assert.False(secondExit.IsCompleted);

        request.SetResult(17);
        await Task.WhenAll(firstExit, secondExit).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(cleanup.Task.IsCompleted);
        Assert.True((bool)Get(service, "_exitRequested"));
        Assert.Same(cleanup.Task, Get(service, "_logoffOperation"));
        var start = (Task)Invoke(service, "StartSharedAsync", (int?)null);
        Assert.True(start.IsCompletedSuccessfully);
        cleanup.SetResult();
    });

    [Fact]
    public Task ExitDoesNotTreatRejectedLogoffRequestAsAccepted() => RunOnStaAsync(async () =>
    {
        var service = CreateServiceSkeleton();
        Set(service, "_dispatcher", Dispatcher.CurrentDispatcher);
        var request = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(service, "_logoffRequest", request.Task);
        Set(service, "_logoffOperation", cleanup.Task);
        var exit = service.RequestLogoffForExitAsync();
        request.SetException(new System.ComponentModel.Win32Exception(5));
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => exit);
        cleanup.SetResult();
    });

    [Fact]
    public Task OldInitialRetryCannotClearReplacementAttempt() => RunOnStaAsync(async () =>
    {
        // Never construct or dispose an RDP window: Dispose logs off a real user session.
        using var settings = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var sessionCancellation = new CancellationTokenSource();
        var service = CreateServiceSkeleton();
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
        var service = CreateServiceSkeleton();
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
    public void PendingSettingsForNextConnectionAreNotAnActiveReconnect()
    {
        var service = CreateServiceSkeleton();
        var pendingField = Field(service, "_pendingRdpSettingChanges");
        pendingField.SetValue(service, Enum.ToObject(pendingField.FieldType, 2));
        Assert.False((bool)Invoke(service, "IsConnectionAttemptInProgress"));
        Set(service, "_rdpSettingsReconnectActive", true);
        Assert.True((bool)Invoke(service, "IsConnectionAttemptInProgress"));
    }

    [Fact]
    public void MissingSettingsReconnectCallbackExpiresOnceAndPreservesPendingSettings()
    {
        var service = CreateServiceSkeleton();
        var pending = Field(service, "_pendingRdpSettingChanges");
        pending.SetValue(service, Enum.ToObject(pending.FieldType, 2));
        Set(service, "_rdpSettingsReconnectActive", true);
        Set(service, "_rdpSettingsReconnectRetriesRemaining", 3);
        Set(service, "_attemptDeadlineTick", Environment.TickCount64 - 1);
        Assert.True((bool)Invoke(service, "ExpireSettingsReconnect"));
        Assert.False((bool)Get(service, "_rdpSettingsReconnectActive"));
        Assert.Equal(0, (int)Get(service, "_rdpSettingsReconnectRetriesRemaining"));
        Assert.Equal(1, (int)Get(service, "_settingsReconnectGeneration"));
        Assert.Equal(2, Convert.ToInt32(Get(service, "_pendingRdpSettingChanges"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.False((bool)Invoke(service, "ExpireSettingsReconnect"));
    }

    [Fact]
    public void RetryDelayAndFreshAttemptDoNotExpireUsingOldDeadline()
    {
        var service = CreateServiceSkeleton();
        Set(service, "_rdpSettingsReconnectActive", true);
        Set(service, "_rdpSettingsReconnectRetryInProgress", true);
        Set(service, "_attemptDeadlineTick", Environment.TickCount64 - 1);
        Assert.False((bool)Invoke(service, "ExpireSettingsReconnect"));
        Set(service, "_rdpSettingsReconnectRetryInProgress", false);
        Invoke(service, "MarkAttemptStarted");
        Assert.False((bool)Invoke(service, "ExpireSettingsReconnect"));
    }

    private static ChildSessionService CreateServiceSkeleton()
    {
        var service = (ChildSessionService)RuntimeHelpers.GetUninitializedObject(typeof(ChildSessionService));
        Set(service, "_desktopHost", new DesktopSessionHost(
            () => throw new InvalidOperationException("This lifecycle fixture must not create a view."),
            new SessionSettings(), () => null));
        return service;
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
