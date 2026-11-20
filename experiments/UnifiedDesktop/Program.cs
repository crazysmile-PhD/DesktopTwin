using DesktopTwin.Experiments.UnifiedDesktop;

// Fully deterministic, effectful simulation. No WTS/COM/SCM calls are made by this executable.
var world = new DesktopObservation(1, 7, true, RdpState.Connected, ServiceState.Running, StreamerState.Child);
var model = new DesktopModel();
var decisions = 0;
void Apply(DesktopEvent input)
{
    var decision = DesktopMachine.Step(model, input);
    model = decision.Model;
    Console.WriteLine($"{input.GetType().Name,-18} goal={model.Goal,-7} recovery={model.Recovery,-12} effect={decision.Command?.Kind.ToString() ?? "none"}");
    while (decision.Command is { } command)
    {
        if (++decisions > 100) throw new InvalidOperationException("Simulation failed to converge");
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
            _ => throw new ArgumentOutOfRangeException()
        };
        world = world with { Revision = world.Revision + 1 };
        Console.WriteLine($"  execute {command.Kind} -> RDP={world.Rdp} child={world.ChildSession?.ToString() ?? "none"} UU={world.Streamer}");
        decision = DesktopMachine.Step(model, new CommandFinished(command.Id, Acknowledgement.Accepted, world));
        model = decision.Model;
    }
}

Apply(new Observe(world));
Apply(new ChooseGoal(DesktopGoal.Host));
if (world.Rdp != RdpState.Disconnected) throw new InvalidOperationException("HOST reconnected RDP");
Apply(new ChooseGoal(DesktopGoal.Child));
Apply(new RepairService());
Apply(new ChooseGoal(DesktopGoal.Stopped));
// Simulate a late callback from a previously connected desktop. STOPPED must remain the goal.
Apply(new Observe(world with { Revision = world.Revision - 1, ChildSession = 7, Rdp = RdpState.Connected }));
if (world.ChildSession is not null || model.Goal != DesktopGoal.Stopped || !model.Quiescent)
    throw new InvalidOperationException("Late observation resurrected the child");
Console.WriteLine($"PASS: one owner, {decisions} serialized commands, no background RDP restore, final={model.Goal}");
