using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionLogoffOperationTests
{
    [Fact]
    public async Task ExitCanFinishAfterRequestWhileWindowsIsStillCleaningUp()
    {
        var request = Task.FromResult<uint?>(17);
        var time = new ManualSessionTime();
        var currentSession = 17;
        var operation = new SessionLogoffOperation(request, () =>
        {
            var current = Volatile.Read(ref currentSession);
            return current < 0 ? null : (uint?)current;
        },
            TimeSpan.FromSeconds(5), time, CancellationToken.None);

        Assert.False(operation.Completion.IsCompleted);
        Assert.Equal((uint?)17, await operation.Request.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(operation.Completion.IsCompleted);

        Volatile.Write(ref currentSession, -1);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FailedRequestIsNotReportedAsAcceptedOrCompleted()
    {
        var failure = new InvalidOperationException("request rejected");
        var operation = new SessionLogoffOperation(Task.FromException<uint?>(failure),
            () => throw new InvalidOperationException("must not query after rejected request"),
            TimeSpan.FromSeconds(5), new ManualSessionTime(), CancellationToken.None);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Request));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Completion));
    }

    [Fact]
    public async Task MissingSessionNeedsNoCompletionPolling()
    {
        var operation = new SessionLogoffOperation(Task.FromResult<uint?>(null),
            () => throw new InvalidOperationException("must not query without a session"),
            TimeSpan.FromSeconds(5), new ManualSessionTime(), CancellationToken.None);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReplacementSessionIsNotWaitedOn()
    {
        var operation = new SessionLogoffOperation(Task.FromResult<uint?>(17), () => 18,
            TimeSpan.FromSeconds(5), new ManualSessionTime(), CancellationToken.None);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CompletionObservationHasABoundedTimeout()
    {
        var operation = new SessionLogoffOperation(Task.FromResult<uint?>(17), () => 17,
            TimeSpan.Zero, new ManualSessionTime(), CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => operation.Completion);
        Assert.True(operation.Request.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposingStopsObservationWithoutCancellingAcceptedRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var operation = new SessionLogoffOperation(Task.FromResult<uint?>(17), () => 17,
            TimeSpan.FromSeconds(5), new ManualSessionTime(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion);
        Assert.Equal((uint?)17, await operation.Request);
    }
}
