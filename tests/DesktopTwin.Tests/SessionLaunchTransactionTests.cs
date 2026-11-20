using System.IO;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionLaunchTransactionTests
{
    [Fact]
    public async Task TargetIsAuthorizedOnlyAfterVerifiedDeletion()
    {
        var calls = new List<string>();
        Task Step(string name) { calls.Add(name); return Task.CompletedTask; }
        await SessionLaunchTransaction.ExecuteAsync(() => Step("registered"), () => Step("connected"),
            () => Step("deleted-and-verified"), () => Step("target-ready"), default);
        Assert.Equal(new[] { "registered", "connected", "deleted-and-verified", "target-ready" }, calls);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("connect")]
    public async Task AmbiguousRegistrationOrConnectionFailureIsCleanedInSameOperation(string failAt)
    {
        var deleted = false;
        var authorized = false;
        await Assert.ThrowsAsync<IOException>(() => SessionLaunchTransaction.ExecuteAsync(
            () => failAt == "register" ? Task.FromException(new IOException("accepted, reply lost")) : Task.CompletedTask,
            () => Task.FromException(new IOException("no connection")),
            () => { deleted = true; return Task.CompletedTask; },
            () => { authorized = true; return Task.CompletedTask; }, default));
        Assert.True(deleted);
        Assert.False(authorized);
    }

    [Fact]
    public async Task FailedDeletionNeverAuthorizesTargetAndPreservesBothErrors()
    {
        var authorized = false;
        var attempts = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SessionLaunchTransaction.ExecuteAsync(
            () => Task.CompletedTask, () => Task.CompletedTask,
            () => { attempts++; throw new IOException("scheduler unavailable"); },
            () => { authorized = true; return Task.CompletedTask; }, default));
        Assert.False(authorized);
        Assert.Equal(2, attempts);
        Assert.IsType<AggregateException>(error.InnerException);
    }

    [Fact]
    public async Task CancellationAfterRegistrationStillWaitsForDeletion()
    {
        using var cancel = new CancellationTokenSource();
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorized = false;
        var operation = SessionLaunchTransaction.ExecuteAsync(
            () => { cancel.Cancel(); return Task.CompletedTask; }, () => Task.CompletedTask,
            async () => { deleting.SetResult(); await release.Task; },
            () => { authorized = true; return Task.CompletedTask; }, cancel.Token);
        await deleting.Task;
        Assert.False(operation.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(authorized);
    }

    [Fact]
    public async Task TargetFailureIsReportedAfterCleanupAlreadyCompleted()
    {
        var deleted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SessionLaunchTransaction.ExecuteAsync(
            () => Task.CompletedTask, () => Task.CompletedTask,
            () => { deleted = true; return Task.CompletedTask; },
            () => { Assert.True(deleted); throw new InvalidOperationException("process exited"); }, default));
    }

    [Theory]
    [InlineData("DesktopTwin-Backup", false)]
    [InlineData("DesktopTwin-SessionLaunch-not-a-guid", false)]
    [InlineData("DesktopTwin-SessionAgent-0123456789abcdef0123456789abcdef", true)]
    [InlineData("DesktopTwin-SessionLaunch-0123456789abcdef0123456789abcdef", true)]
    public void LegacyCleanupOnlyOwnsExactTemporaryTaskNames(string name, bool owned) =>
        Assert.Equal(owned, ChildSessionProcessLauncher.IsOwnedTemporaryTask(name));
}
