using System.IO;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class AsyncSnapshotRefreshTests
{
    [Fact]
    public async Task RenderingFailurePropagatesWithoutBeingReportedAsCollectionFailure()
    {
        var error = new InvalidOperationException("rendering failed");
        var collectionFailures = 0;
        var renderCalls = 0;
        using var refresh = new AsyncSnapshotRefresh<int>(() => Task.FromResult(1),
            _ => { if (++renderCalls == 1) throw error; }, _ => collectionFailures++);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(refresh.RequestAsync));
        Assert.Equal(0, collectionFailures);
        await refresh.RequestAsync();
        Assert.Equal(2, renderCalls);
    }

    [Fact]
    public async Task RequestsDuringCollectionCoalesceAndDiscardOldSnapshot()
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var published = new List<int>();
        using var refresh = new AsyncSnapshotRefresh<int>(() =>
        {
            calls++;
            if (calls == 1) return first.Task;
            secondStarted.SetResult();
            return second.Task;
        }, published.Add, _ => Assert.Fail("Unexpected collection failure"));
        var pending = refresh.RequestAsync();
        Assert.False(pending.IsCompleted);
        for (var i = 0; i < 100; i++) Assert.Same(pending, refresh.RequestAsync());
        Assert.Equal(1, calls);
        first.SetResult(1);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(published);
        second.SetResult(2);
        await pending;
        Assert.Equal(2, calls);
        Assert.Equal(new[] { 2 }, published);
    }

    [Fact]
    public async Task ClosingDiscardsLateSuccessAndFailure()
    {
        foreach (var fail in new[] { false, true })
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var refresh = new AsyncSnapshotRefresh<int>(() => completion.Task,
                _ => Assert.Fail("Closed view received data"), _ => Assert.Fail("Closed view received error"));
            var pending = refresh.RequestAsync();
            refresh.Dispose();
            if (fail) completion.SetException(new IOException("test"));
            else completion.SetResult(1);
            await pending;
            await refresh.RequestAsync();
        }
    }

    [Fact]
    public async Task FailureDoesNotDisableNextRefresh()
    {
        var calls = 0;
        var failures = 0;
        var published = 0;
        using var refresh = new AsyncSnapshotRefresh<int>(
            () => ++calls == 1 ? Task.FromException<int>(new IOException("test")) : Task.FromResult(3),
            value => published = value, _ => failures++);
        await refresh.RequestAsync();
        await refresh.RequestAsync();
        Assert.Equal(1, failures);
        Assert.Equal(3, published);
    }
}
