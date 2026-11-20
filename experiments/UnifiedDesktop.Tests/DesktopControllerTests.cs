using System.Threading.Channels;
using DesktopTwin.Experiments.UnifiedDesktop;

namespace DesktopTwin.Experiments.Tests;

public sealed class DesktopControllerTests
{
    private static DesktopObservation Empty(long revision) => new(revision, null, false,
        RdpState.Disconnected, ServiceState.Running, StreamerState.Host);

    [Fact]
    public async Task BlockingDeviceCallDoesNotBlockLogoffMessagesOrOverlapAnotherCommand()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new BlockingPlatform(entered, release);
        await using var controller = new DesktopController(platform);
        await controller.PostAsync(new Observe(Empty(1)));
        await controller.PostAsync(new ChooseGoal(DesktopGoal.Child));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var stopped = await controller.PostAsync(new ChooseGoal(DesktopGoal.Stopped))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(DesktopGoal.Stopped, stopped.Goal);
            Assert.NotNull(stopped.InFlight);
            Assert.Equal(1, platform.Calls);
        }
        finally { release.Set(); }
        await WaitFor(controller, model => model.Quiescent);
    }

    [Fact]
    public async Task CloseDrainsAnAcceptedServiceStopInsteadOfDiscardingTheStart()
    {
        var platform = new ControlledPlatform();
        var controller = new DesktopController(platform);
        await controller.PostAsync(new Observe(Empty(1)));
        await controller.PostAsync(new RepairService());
        var stop = await platform.Next();
        var closing = controller.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        stop.Reply.SetResult(new(Acknowledgement.Accepted, Empty(2) with
            { Service = ServiceState.Stopped, Streamer = StreamerState.Absent }));
        var start = await platform.Next();
        Assert.Equal(CommandKind.StartService, start.Command.Kind);
        Assert.False(closing.IsCompleted);
        start.Reply.SetResult(new(Acknowledgement.Accepted, Empty(3)));
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, platform.MaximumActive);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.InspectAsync());
    }

    [Fact]
    public async Task CancellationBeforeAdmissionDoesNotChangeTheGoal()
    {
        await using var controller = new DesktopController(new ControlledPlatform());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.PostAsync(new ChooseGoal(DesktopGoal.Child), new CancellationToken(true)));
        Assert.Equal(DesktopGoal.Observe, (await controller.InspectAsync()).Goal);
    }

    [Fact]
    public async Task DeviceAdapterCanReportBackThroughTheMailboxWithoutAWaitCycle()
    {
        DesktopController controller = null!;
        controller = new DesktopController(new CallbackPlatform(async _ =>
        {
            // A native callback can reenter observation handling while the command is still in flight.
            await controller.PostAsync(new Observe(Empty(2))).WaitAsync(TimeSpan.FromSeconds(2));
            return new(Acknowledgement.Rejected, Empty(3));
        }));
        await using (controller)
        {
            await controller.PostAsync(new Observe(Empty(1)));
            await controller.PostAsync(new ChooseGoal(DesktopGoal.Child));
            await WaitFor(controller, model => model.InFlight is null);
            Assert.Equal(3, (await controller.InspectAsync()).Observed.Revision);
        }
    }

    private static async Task WaitFor(DesktopController controller, Func<DesktopModel, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate(await controller.InspectAsync().WaitAsync(timeout.Token)))
            await Task.Delay(1, timeout.Token);
    }

    private sealed class BlockingPlatform(TaskCompletionSource entered, ManualResetEventSlim release) : IDesktopPlatform
    {
        internal int Calls;
        public Task<CommandResult> ExecuteAsync(DesktopCommand command)
        {
            Interlocked.Increment(ref Calls);
            entered.TrySetResult();
            release.Wait(); // Deliberately blocks synchronously before returning a Task.
            return Task.FromResult(new CommandResult(Acknowledgement.Rejected, Empty(2)));
        }
    }

    private sealed class CallbackPlatform(Func<DesktopCommand, Task<CommandResult>> execute) : IDesktopPlatform
    {
        public Task<CommandResult> ExecuteAsync(DesktopCommand command) => execute(command);
    }

    private sealed class ControlledPlatform : IDesktopPlatform
    {
        private readonly Channel<(DesktopCommand Command, TaskCompletionSource<CommandResult> Reply)> _commands =
            Channel.CreateUnbounded<(DesktopCommand, TaskCompletionSource<CommandResult>)>();
        private int _active;
        internal int MaximumActive;
        public async Task<CommandResult> ExecuteAsync(DesktopCommand command)
        {
            MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref _active));
            var reply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _commands.Writer.WriteAsync((command, reply));
            try { return await reply.Task; }
            finally { Interlocked.Decrement(ref _active); }
        }
        internal async Task<(DesktopCommand Command, TaskCompletionSource<CommandResult> Reply)> Next() =>
            await _commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
