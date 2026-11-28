using DesktopTwin.Input;

namespace DesktopTwin.Tests;

/// <summary>The notification buffer may drop redundant wakes, while the separate packet queue retains all input.</summary>
public sealed class InputWakeSignalTests
{
    [Fact]
    public void ClearingAnOldNotificationPreservesTheNextRacingSignal()
    {
        using var signal = new InputWakeSignal();
        signal.Set();
        Assert.True(signal.ClearPending());
        Assert.False(signal.ClearPending());
        var pending = signal.WaitAsync(Timeout.Infinite);
        Assert.False(pending.IsCompleted);
        signal.Set();
        Assert.Equal(0, Completed(pending));
    }

    [Fact]
    public void BurstsCoalesceAndNewSignalsAndDisposalStillCompleteAPendingWait()
    {
        using var signal = new InputWakeSignal();
        for (var i = 0; i < 10000; i++) signal.Set();
        Assert.Equal(0, Completed(signal.WaitAsync(Timeout.Infinite)));
        var pending = signal.WaitAsync(Timeout.Infinite);
        Assert.False(pending.IsCompleted);
        for (var i = 0; i < 10000; i++) signal.Set();
        Assert.Equal(0, Completed(pending));
        // One in-flight read and at most one buffered notification cover the entire burst.
        Assert.Equal(0, Completed(signal.WaitAsync(Timeout.Infinite)));
        var next = signal.WaitAsync(Timeout.Infinite);
        Assert.False(next.IsCompleted);
        signal.Set();
        Assert.Equal(0, Completed(next));
        var last = signal.WaitAsync(Timeout.Infinite);
        Assert.False(last.IsCompleted);
        signal.Dispose();
        Assert.Equal(0, Completed(last));
        Assert.Throws<ObjectDisposedException>(() => Completed(signal.WaitAsync(Timeout.Infinite)));
    }

    [Fact]
    public void WarmBufferedPendingAndTimedWaitsDoNotAllocatePerNotification()
    {
        using var signal = new InputWakeSignal();
        for (var i = 0; i < 1000; i++) RoundTrip(signal);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var i = 0; i < 10000; i++) checksum += RoundTrip(signal);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task DeadlineCanBeRearmedAndIsDisabledWhenTheReaderReturnsToIdle()
    {
        using var signal = new InputWakeSignal();
        Assert.Equal(0, Completed(signal.WaitAsync(0)));
        await signal.WaitAsync(10).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var idle = signal.WaitAsync(Timeout.Infinite);
        await Task.Delay(60);
        Assert.False(idle.IsCompleted);
        signal.Set();
        Assert.Equal(0, Completed(idle));
        await signal.WaitAsync(10).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static int RoundTrip(InputWakeSignal signal)
    {
        signal.Set();
        var buffered = Completed(signal.WaitAsync(Timeout.Infinite));
        // A distant deadline exercises timer reuse without making wall-clock speed a test condition.
        var pending = signal.WaitAsync(60000);
        if (pending.IsCompleted) throw new InvalidOperationException("Unexpected notification.");
        signal.Set();
        return buffered + Completed(pending);
    }

    private static byte Completed(ValueTask<byte> value)
    {
        if (!value.IsCompletedSuccessfully) throw new InvalidOperationException("Notification did not complete.");
        return value.Result;
    }
}
