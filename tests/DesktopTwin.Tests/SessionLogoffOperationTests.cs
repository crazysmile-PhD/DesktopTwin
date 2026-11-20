using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionLogoffOperationTests
{
    [Fact]
    public async Task ExitCanFinishAfterRequestWhileWindowsIsStillCleaningUp()
    {
        var request = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentSession = 17;
        var operation = new SessionLogoffOperation(request.Task, () =>
        {
            var current = Volatile.Read(ref currentSession);
            return current < 0 ? null : (uint?)current;
        },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(operation.Request.IsCompleted);
        Assert.False(operation.Completion.IsCompleted);
        request.SetResult(17);
        Assert.Equal((uint?)17, await operation.Request.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(operation.Completion.IsCompleted);

        Volatile.Write(ref currentSession, -1);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FailedRequestIsNotReportedAsAcceptedOrCompleted()
    {
        var failure = new InvalidOperationException("request rejected");
        var operation = new SessionLogoffOperation(Task.FromException<uint?>(failure),
            () => throw new Exception("must not query after rejected request"),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Request));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operation.Completion));
    }

    [Fact]
    public async Task MissingSessionNeedsNoCompletionPolling()
    {
        var operation = new SessionLogoffOperation(Task.FromResult<uint?>(null),
            () => throw new Exception("must not query without a session"),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReplacementSessionIsNotWaitedOn()
    {
        var operation = new SessionLogoffOperation(Task.FromResult<uint?>(17), () => 18,
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CompletionObservationHasABoundedTimeout()
    {
        var operation = new SessionLogoffOperation(Task.FromResult<uint?>(17), () => 17,
            TimeSpan.Zero, CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => operation.Completion);
        Assert.True(operation.Request.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposingStopsObservationWithoutCancellingAcceptedRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var operation = new SessionLogoffOperation(Task.FromResult<uint?>(17), () => 17,
            TimeSpan.FromSeconds(5), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.Completion);
        Assert.Equal((uint?)17, await operation.Request);
    }
}
