using System.Reflection;
using System.Runtime.CompilerServices;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class UuOperationTests
{
    [Fact]
    public async Task ConcurrentCommandsAreDeferredUntilOwnerFinishes()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<UuViewSwitchService.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = 0;
        fixture.Service.BecameIdle += (_, _) => Interlocked.Increment(ref idle);
        var first = fixture.Run(_ => { started.SetResult(); return finish.Task; });
        await started.Task;
        var concurrent = await fixture.Run(_ => throw new InvalidOperationException("Must not run"), automatic: true);
        Assert.True(concurrent.Deferred);
        Assert.Equal(0, idle);
        finish.SetResult(new(true, "done"));
        Assert.True((await first).Success);
        Assert.Equal(1, idle);
        Assert.True((await fixture.Run(_ => Task.FromResult(new UuViewSwitchService.Result(true, "next")))).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoffOrDisposeCancelsRunningCommandWithoutDisposingItsGate(bool dispose)
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = fixture.Run(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(true, "unreachable");
        });
        await started.Task;
        if (dispose) fixture.Service.Dispose();
        else fixture.Session.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.False(result.Deferred);
    }

    [Fact]
    public async Task ManualCommandWaitsForCancelledAutomaticOwner()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var automatic = fixture.Run(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(true, "unreachable");
        }, automatic: true, cancellation.Token);
        await started.Task;
        var manual = fixture.Run(_ => Task.FromResult(new UuViewSwitchService.Result(true, "manual")));
        Assert.False(manual.IsCompleted);
        cancellation.Cancel();
        Assert.False((await automatic).Success);
        Assert.True((await manual.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly CancellationTokenSource Session = new();
        internal readonly UuViewSwitchService Service;

        internal Fixture()
        {
            // ExecuteAsync itself does not touch Windows sessions; fake only its lifetime source.
            var child = (ChildSessionService)RuntimeHelpers.GetUninitializedObject(typeof(ChildSessionService));
            Field("_environmentGate").SetValue(child, new object());
            Field("_sessionCancellation").SetValue(child, Session);
            Service = new UuViewSwitchService(child);
        }

        internal Task<UuViewSwitchService.Result> Run(Func<CancellationToken, Task<UuViewSwitchService.Result>> action,
            bool automatic = false, CancellationToken cancellationToken = default)
        {
            var method = typeof(UuViewSwitchService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("ExecuteAsync");
            return method.Invoke(Service, new object[] { action, cancellationToken, automatic }) as Task<UuViewSwitchService.Result>
                ?? throw new InvalidOperationException("No operation task");
        }

        private static FieldInfo Field(string name) => typeof(ChildSessionService).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(name);

        public void Dispose()
        {
            Service.Dispose();
            Session.Dispose();
        }
    }
}
