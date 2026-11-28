using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SfcVerificationQueueTests
{
    [Fact]
    public async Task ObsoleteOrBusyRequestsCannotStartVerification()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var executions = 0;
        Assert.Equal(-1, await SfcVerificationQueue.RunAsync(gate, TimeSpan.Zero,
            () => false, () => ++executions, default));
        Assert.Equal(-1, await SfcVerificationQueue.RunAsync(gate, TimeSpan.Zero,
            () => true, () => ++executions, default));
        Assert.Equal(0, executions);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public async Task SupersededWaiterCannotStartSfcAfterTheGateOpens()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var current = true;
        var executions = 0;
        var obsolete = SfcVerificationQueue.RunAsync(gate, TimeSpan.FromSeconds(5),
            () => current, () => ++executions, default);
        Assert.False(obsolete.IsCompleted);
        current = false;
        var replacement = SfcVerificationQueue.RunAsync(gate, TimeSpan.FromSeconds(5),
            () => true, () => ++executions, default);
        gate.Release();

        Assert.Equal(-1, await obsolete);
        Assert.Equal(1, await replacement);
        Assert.Equal(1, executions);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task CanceledWaiterCompletesWithoutWaitingForRunningVerification()
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource();
        var executions = 0;
        var task = SfcVerificationQueue.RunAsync(gate, TimeSpan.FromSeconds(5),
            () => true, () => ++executions, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, executions);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public async Task VerificationFailureReleasesTheGateForTheNextRequest()
    {
        using var gate = new SemaphoreSlim(1, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SfcVerificationQueue.RunAsync(gate,
            TimeSpan.FromSeconds(5), () => true, () => throw new InvalidOperationException("synthetic SFC failure"), default));
        Assert.Equal(0, await SfcVerificationQueue.RunAsync(gate, TimeSpan.FromSeconds(5), () => true, () => 0, default));
        Assert.Equal(1, gate.CurrentCount);
    }
}
