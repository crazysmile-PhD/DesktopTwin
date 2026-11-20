using DesktopTwin.Experiments.UnifiedDesktop;

namespace DesktopTwin.Experiments.Tests;

public sealed class InterleavingTests
{
    [Fact]
    public void LatestLogoffConvergesAcross512SeededTracesWith64EventsEach()
    {
        for (var seed = 0; seed < 512; seed++)
        {
            var random = new Random(seed);
            var world = new DesktopObservation(1, 7, true, RdpState.Connected,
                ServiceState.Running, StreamerState.Child);
            var model = DesktopMachine.Step(new(), new Observe(world)).Model;
            void Send(DesktopEvent input)
            {
                var previous = model.InFlight;
                var decision = DesktopMachine.Step(model, input);
                model = decision.Model;
                if (decision.Command is { } issued)
                {
                    Assert.True(previous is null || input is CommandFinished finished && finished.Id == previous.Id,
                        $"seed {seed}: overlapped device commands");
                    Assert.Equal(issued, model.InFlight);
                    if (issued.Kind == CommandKind.ConnectChild)
                        Assert.Equal(DesktopGoal.Child, model.Goal);
                }
            }
            void Complete()
            {
                if (model.InFlight is not { } command) return;
                var rejected = command.Kind is CommandKind.LogoffChild or CommandKind.ConnectChild
                    && command.Session is { } id && id != world.ChildSession;
                if (!rejected)
                    world = command.Kind switch
                    {
                        CommandKind.DisconnectRdp => world with { Rdp = RdpState.Disconnected, ChildActive = false },
                        CommandKind.ConnectChild => world with { ChildSession = world.ChildSession ?? 8,
                            ChildActive = true, Rdp = RdpState.Connected },
                        CommandKind.LogoffChild => world with { ChildSession = null, ChildActive = false },
                        CommandKind.RecycleStreamer => world with { Streamer = world.ChildActive == true
                            ? StreamerState.Child : StreamerState.Host },
                        CommandKind.StopService => world with { Service = ServiceState.Stopped, Streamer = StreamerState.Absent },
                        CommandKind.StartService => world with { Service = ServiceState.Running,
                            Streamer = world.ChildActive == true ? StreamerState.Child : StreamerState.Host },
                        _ => world
                    };
                world = world with { Revision = world.Revision + 1 };
                Send(new CommandFinished(command.Id, rejected ? Acknowledgement.Rejected : Acknowledgement.Accepted, world));
            }
            for (var i = 0; i < 64; i++)
            {
                switch (random.Next(8))
                {
                    case 0: Send(new ChooseGoal(DesktopGoal.Host)); break;
                    case 1: Send(new ChooseGoal(DesktopGoal.Child)); break;
                    case 2: Send(new ChooseGoal(DesktopGoal.Stopped)); break;
                    case 3: Send(new RepairService()); break;
                    case 4: Send(new Tick()); break;
                    case 5: Complete(); break;
                    case 6:
                        world = world with { Revision = world.Revision + 1, ChildSession = null,
                            ChildActive = false, Rdp = RdpState.Disconnected, Streamer = StreamerState.Host };
                        Send(new Observe(world));
                        break;
                    case 7:
                        var before = model.Observed;
                        Send(new Observe(world with { Revision = 0, ChildSession = 99, Rdp = RdpState.Connected }));
                        Assert.Equal(before, model.Observed);
                        break;
                }
            }
            Send(new ChooseGoal(DesktopGoal.Stopped));
            // Fair environment: issued commands settle and observations eventually arrive.
            for (var i = 0; i < 24 && (world.ChildSession is not null || world.Rdp != RdpState.Disconnected
                || !model.Quiescent || model.InFlight is not null); i++)
            {
                if (model.InFlight is not null) Complete();
                else if (model.Fault is not null) Send(new Retry());
                else Send(new Tick());
            }
            Assert.True(world.ChildSession is null && world.Rdp == RdpState.Disconnected && model.Quiescent,
                $"seed {seed}: failed to settle, {model}");
            Assert.Equal(DesktopGoal.Stopped, model.Goal);
        }
    }
}
