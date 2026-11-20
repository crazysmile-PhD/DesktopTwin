using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class CredentialDialogWatchTests
{
    [Fact]
    public async Task StoppingBeforeQueuedWorkStartsDoesNotUseADisposedSource()
    {
        Func<Task>? queued = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = new CredentialDialogWatch(work => { queued = work; return completed.Task; });
        var calls = 0;
        watch.Start(_ => { calls++; return Task.CompletedTask; });
        Assert.True(watch.IsStarted);
        watch.Start(_ => throw new InvalidOperationException("must not queue a duplicate"));
        watch.Stop();
        watch.Dispose();
        Assert.False(watch.IsStarted);
        await queued!().WaitAsync(TimeSpan.FromSeconds(5));
        completed.SetResult();
        await watch.Completion;
        Assert.Equal(0, calls);
        watch.Start(_ => throw new InvalidOperationException("must not start after disposal"));
    }

    [Fact]
    public async Task RestartWaitsForCanceledWorkToDrainAndOldTokenStaysCanceled()
    {
        using var watch = new CredentialDialogWatch();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = false;
        watch.Start(async token =>
        {
            entered.SetResult(token);
            await drain.Task;
            if (!token.IsCancellationRequested) submitted = true;
        });
        var oldToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watch.Stop();
        watch.Start(_ => { nextEntered.SetResult(); return Task.CompletedTask; });
        Assert.True(oldToken.IsCancellationRequested);
        Assert.False(nextEntered.Task.IsCompleted);
        drain.SetResult();
        await watch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(nextEntered.Task.IsCompletedSuccessfully);
        Assert.False(submitted);
        // Natural completion must not reset the once-per-connection retry budget.
        watch.Start(_ => throw new InvalidOperationException("must not restart a completed connection"));
        watch.Stop();
        watch.Stop();
    }

    [Fact]
    public async Task FaultsAndCancellationAreObservedAndDoNotPoisonTheNextConnection()
    {
        using var watch = new CredentialDialogWatch();
        watch.Start(_ => throw new InvalidOperationException("synthetic watcher failure"));
        await watch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        watch.Stop();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watch.Start(async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watch.Dispose();
        await watch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(watch.Completion.IsCompletedSuccessfully);
    }
}
