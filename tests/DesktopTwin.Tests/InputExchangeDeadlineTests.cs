using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>Native deadline lifetime, renewal and callback disposal retain the transport's cancellation contract.</summary>
public sealed class InputExchangeDeadlineTests
{
    [Fact]
    public void NativeAllocationFailuresAreReportedWithoutRetainingTheOwner()
    {
        using var stop = new CancellationTokenSource();
        var failure = Assert.Throws<Win32Exception>(() => new InputExchangeDeadline(stop.Token, 1000,
            (_, _) => { Marshal.SetLastPInvokeError(8); return IntPtr.Zero; }));
        Assert.Equal(8, failure.NativeErrorCode);
        var expected = new IOException("synthetic allocation failure");
        Assert.Same(expected, Assert.Throws<IOException>(() =>
            new InputExchangeDeadline(stop.Token, 1000, (_, _) => throw expected)));
        stop.Cancel();
    }

    [Fact]
    public void NativeContextDoesNotRootAnAbandonedOwnerAndItsCallbackIsHarmless()
    {
        var abandoned = AbandonOwner();
        using var handle = abandoned.Handle;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(abandoned.Owner.IsAlive);
        var context = Assert.IsType<GCHandle>(Field(handle.GetType(), "_context").GetValue(handle));
        var callback = Assert.IsAssignableFrom<Delegate>(Field(handle.GetType(), "Callback", true).GetValue(null));
        callback.DynamicInvoke(IntPtr.Zero, GCHandle.ToIntPtr(context), handle.DangerousGetHandle());
        GC.KeepAlive(handle);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, SafeHandle Handle) AbandonOwner()
    {
        var deadline = new InputExchangeDeadline(default);
        var timer = Assert.IsAssignableFrom<SafeHandle>(Field(typeof(InputExchangeDeadline), "_timer").GetValue(deadline));
        return (new WeakReference(deadline), timer);
    }

    private static FieldInfo Field(Type type, string name, bool isStatic = false)
        => Assert.IsAssignableFrom<FieldInfo>(type.GetField(name, BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance)));

    [Fact]
    public async Task CompletedExchangesDisarmTheDeadlineAndLeaveIdleUncancelled()
    {
        using var deadline = new InputExchangeDeadline(default, 50);
        for (var i = 0; i < 1000; i++) { deadline.Begin(); deadline.End(); }
        await Task.Delay(150);
        Assert.False(deadline.Token.IsCancellationRequested);
        deadline.Begin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.Infinite, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        deadline.End();
        Assert.ThrowsAny<OperationCanceledException>(deadline.Begin);
    }

    [Fact]
    public async Task RenewalSurvivesTheOldDeadlineButStillExpires()
    {
        using var deadline = new InputExchangeDeadline(default, 1000);
        deadline.Begin();
        await Task.Delay(600);
        deadline.End();
        deadline.Begin();
        await Task.Delay(600);
        Assert.False(deadline.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.Infinite, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void QueuedCallbacksCannotExpireANewerOrIdleExchange()
    {
        using var deadline = new InputExchangeDeadline(default, 60000);
        deadline.Begin();
        deadline.End();
        deadline.Observe();
        Assert.False(deadline.Token.IsCancellationRequested);
        deadline.Begin();
        deadline.Observe();
        Assert.False(deadline.Token.IsCancellationRequested);
        deadline.End();
        deadline.Dispose();
        deadline.Observe();
    }

    [Fact]
    public async Task DisposingFromTheCancellationCallbackDoesNotWaitOnItself()
    {
        using var deadline = new InputExchangeDeadline(default, 20);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = deadline.Token.Register(() => { deadline.Dispose(); completed.SetResult(); });
        deadline.Begin();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        deadline.End();
        Assert.Throws<ObjectDisposedException>(deadline.Begin);
    }

    [Fact]
    public async Task DisposalAndExpirationCanRaceWithoutTouchingFreedContext()
    {
        for (var i = 0; i < 32; i++)
        {
            using var deadline = new InputExchangeDeadline(default, 10);
            deadline.Begin();
            await Task.Delay(10);
            await Task.Run(deadline.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
            deadline.End();
        }
    }

    [Fact]
    public void CancelledParentsAndInvalidDurationsCannotStartAnExchange()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var deadline = new InputExchangeDeadline(stop.Token);
        Assert.ThrowsAny<OperationCanceledException>(deadline.Begin);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputExchangeDeadline(default, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputExchangeDeadline(default, 60001));
    }
}
