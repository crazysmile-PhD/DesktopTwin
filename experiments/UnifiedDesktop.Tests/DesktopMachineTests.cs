using DesktopTwin.Experiments.UnifiedDesktop;

namespace DesktopTwin.Experiments.Tests;

public sealed class DesktopMachineTests
{
    private static DesktopObservation Host(long revision = 1) => new(revision, 7, false,
        RdpState.Disconnected, ServiceState.Running, StreamerState.Host);
    private static DesktopObservation Child(long revision = 1) => Host(revision) with
        { ChildActive = true, Rdp = RdpState.Connected, Streamer = StreamerState.Child };
    private static DesktopModel With(DesktopObservation observed) =>
        DesktopMachine.Step(new(), new Observe(observed)).Model;

    [Fact]
    public void HostRemainsDisconnectedInsteadOfRestoringRdpInTheBackground()
    {
        var disconnect = DesktopMachine.Step(With(Child()), new ChooseGoal(DesktopGoal.Host));
        Assert.Equal(CommandKind.DisconnectRdp, disconnect.Command!.Kind);
        var recycle = Finish(disconnect, Host(2) with { Streamer = StreamerState.Child });
        Assert.Equal(CommandKind.RecycleStreamer, recycle.Command!.Kind);
        var settled = Finish(recycle, Host(3));
        Assert.Null(settled.Command);
        Assert.True(settled.Model.Quiescent);
        for (var i = 4; i < 20; i++)
        {
            settled = DesktopMachine.Step(settled.Model, new Observe(Host(i)));
            Assert.Null(settled.Command);
        }
    }

    [Fact]
    public void LogoffSupersedesAConnectWithoutLaunchingOverlappingCommands()
    {
        var empty = Host() with { ChildSession = null };
        var connect = DesktopMachine.Step(With(empty), new ChooseGoal(DesktopGoal.Child));
        Assert.True(connect.Command!.AllowCreate);
        var stopped = DesktopMachine.Step(connect.Model, new ChooseGoal(DesktopGoal.Stopped));
        Assert.Null(stopped.Command);
        Assert.Equal(connect.Command, stopped.Model.InFlight);
        // The already issued connect may still succeed. Its actual outcome is incorporated and cleaned up.
        var disconnect = DesktopMachine.Step(stopped.Model,
            new CommandFinished(connect.Command.Id, Acknowledgement.Accepted, Child(2)));
        Assert.Equal(CommandKind.DisconnectRdp, disconnect.Command!.Kind);
        var logoff = Finish(disconnect, Host(3));
        Assert.Equal(CommandKind.LogoffChild, logoff.Command!.Kind);
        var completed = Finish(logoff, empty with { Revision = 4 });
        Assert.Equal(DesktopGoal.Stopped, completed.Model.Goal);
        Assert.Null(completed.Command);
        Assert.True(completed.Model.Quiescent);
        Assert.Null(DesktopMachine.Step(completed.Model, new Observe(Child(2))).Command);
    }

    [Fact]
    public void ExplicitChildGoalDuringLogoffStartsOnlyAfterTheOldSessionIsGone()
    {
        var logoff = DesktopMachine.Step(With(Host()), new ChooseGoal(DesktopGoal.Stopped));
        Assert.Equal(CommandKind.LogoffChild, logoff.Command!.Kind);
        var requested = DesktopMachine.Step(logoff.Model, new ChooseGoal(DesktopGoal.Child));
        Assert.Null(requested.Command);
        var connect = DesktopMachine.Step(requested.Model, new CommandFinished(logoff.Command.Id,
            Acknowledgement.Accepted, Host(2) with { ChildSession = null }));
        Assert.Equal(CommandKind.ConnectChild, connect.Command!.Kind);
        Assert.True(connect.Command.AllowCreate);
    }

    [Fact]
    public void ObservingAnExternallyDeletedChildDoesNotGrantPermissionToRecreateIt()
    {
        var settled = DesktopMachine.Step(With(Child()), new ChooseGoal(DesktopGoal.Child));
        // A goal on an already existing session cannot retain a latent creation ticket.
        var deleted = DesktopMachine.Step(settled.Model,
            new Observe(Host(2) with { ChildSession = null }));
        Assert.Null(deleted.Command);
        Assert.Equal(DesktopGoal.Child, deleted.Model.Goal);
    }

    [Fact]
    public void DuplicatedCompletionCannotAcknowledgeADifferentCommandOrRefreshOldFacts()
    {
        var first = DesktopMachine.Step(With(Child()), new ChooseGoal(DesktopGoal.Host));
        var next = Finish(first, Host(2) with { Streamer = StreamerState.Child });
        Assert.NotNull(next.Command);
        var duplicate = DesktopMachine.Step(next.Model,
            new CommandFinished(first.Command!.Id, Acknowledgement.Accepted, Child(100)));
        Assert.Equal(next.Model, duplicate.Model);
        Assert.Null(duplicate.Command);
    }

    [Fact]
    public void OldRejectedConnectDoesNotBlockANewerLogoffGoal()
    {
        var connect = DesktopMachine.Step(With(Host() with { ChildSession = null }),
            new ChooseGoal(DesktopGoal.Child));
        var stopped = DesktopMachine.Step(connect.Model, new ChooseGoal(DesktopGoal.Stopped));
        var rejected = DesktopMachine.Step(stopped.Model,
            new CommandFinished(connect.Command!.Id, Acknowledgement.Rejected));
        Assert.Null(rejected.Model.Fault);
        Assert.Equal(DesktopGoal.Stopped, rejected.Model.Goal);
        Assert.True(rejected.Model.Quiescent);
    }

    [Fact]
    public void AcceptedDisconnectDoesNotReleaseTheCommandSlotUntilFreshConfirmationArrives()
    {
        var disconnect = DesktopMachine.Step(With(Child()), new ChooseGoal(DesktopGoal.Host));
        var accepted = DesktopMachine.Step(disconnect.Model,
            new CommandFinished(disconnect.Command!.Id, Acknowledgement.Accepted));
        Assert.Equal(disconnect.Command, accepted.Model.InFlight);
        Assert.True(accepted.Model.AwaitingConfirmation);
        Assert.Null(accepted.Command);
        var pending = DesktopMachine.Step(accepted.Model,
            new Observe(Child(2) with { Rdp = RdpState.Disconnecting }));
        Assert.Null(pending.Command);
        var confirmed = DesktopMachine.Step(pending.Model,
            new Observe(Host(3) with { Streamer = StreamerState.Child }));
        Assert.Equal(CommandKind.RecycleStreamer, confirmed.Command!.Kind);
        Assert.False(confirmed.Model.AwaitingConfirmation);
    }

    [Fact]
    public void UnknownConnectKeepsOwnershipUntilLateFactsCanBeCleanedUpUnderTheNewGoal()
    {
        var connect = DesktopMachine.Step(With(Host() with { ChildSession = null }),
            new ChooseGoal(DesktopGoal.Child));
        var uncertain = DesktopMachine.Step(connect.Model,
            new CommandFinished(connect.Command!.Id, Acknowledgement.Unknown, Error: "native reply lost"));
        Assert.False(uncertain.Model.Quiescent);
        var stopped = DesktopMachine.Step(uncertain.Model, new ChooseGoal(DesktopGoal.Stopped));
        Assert.Null(stopped.Command);
        var lateSuccess = DesktopMachine.Step(stopped.Model, new Observe(Child(2)));
        Assert.Equal(DesktopGoal.Stopped, lateSuccess.Model.Goal);
        Assert.Equal(CommandKind.DisconnectRdp, lateSuccess.Command!.Kind);
        Assert.Null(lateSuccess.Model.Fault);
    }

    [Fact]
    public void RejectedExplicitCreateCanBeRetriedWithoutAnAutomaticRetryLoop()
    {
        var connect = DesktopMachine.Step(With(Host() with { ChildSession = null }),
            new ChooseGoal(DesktopGoal.Child));
        var failure = DesktopMachine.Step(connect.Model,
            new CommandFinished(connect.Command!.Id, Acknowledgement.Rejected));
        Assert.Null(failure.Command);
        Assert.NotNull(failure.Model.Fault);
        var retry = DesktopMachine.Step(failure.Model, new Retry());
        Assert.Equal(CommandKind.ConnectChild, retry.Command!.Kind);
        Assert.True(retry.Command.AllowCreate);
    }

    [Theory]
    [InlineData(Acknowledgement.Accepted)]
    [InlineData(Acknowledgement.Unknown)]
    public void AcceptedOrUncertainStopRemainsOwnedAcrossGoalChanges(Acknowledgement outcome)
    {
        var stop = DesktopMachine.Step(With(Host() with { ChildSession = null }), new RepairService());
        var requested = DesktopMachine.Step(stop.Model, new ChooseGoal(DesktopGoal.Stopped));
        var pending = DesktopMachine.Step(requested.Model,
            new CommandFinished(stop.Command!.Id, outcome, Host(2) with { ChildSession = null }));
        Assert.Null(pending.Command); // An immediate RUNNING read must not release the obligation.
        Assert.Equal(RecoveryStage.AwaitStopped, pending.Model.Recovery);
        var start = DesktopMachine.Step(pending.Model, new Observe(Host(3) with
            { ChildSession = null, Service = ServiceState.Stopped, Streamer = StreamerState.Absent }));
        Assert.Equal(CommandKind.StartService, start.Command!.Kind);
        var finished = Finish(start, Host(4) with { ChildSession = null });
        Assert.True(finished.Model.Quiescent);
        Assert.Equal(DesktopGoal.Stopped, finished.Model.Goal);
    }

    [Fact]
    public void RejectedStopAndUnknownQueryDoNotLeaveAPermanentBusyTask()
    {
        var stop = DesktopMachine.Step(With(Host()), new RepairService());
        var rejected = DesktopMachine.Step(stop.Model, new CommandFinished(stop.Command!.Id,
            Acknowledgement.Rejected, Host(2) with { Service = ServiceState.Unknown }));
        var observed = DesktopMachine.Step(rejected.Model, new Observe(Host(3)));
        Assert.True(observed.Model.Quiescent);
        Assert.NotNull(observed.Model.Fault);
        Assert.Null(observed.Command);
        Assert.Equal(CommandKind.StopService,
            DesktopMachine.Step(observed.Model, new RepairService()).Command!.Kind);
    }

    [Fact]
    public void AcceptedPendingStopKeepsTheOnlyCommandSlotWhileTheLatestLogoffGoalIsRecorded()
    {
        var stop = DesktopMachine.Step(With(Host()), new RepairService());
        var requested = DesktopMachine.Step(stop.Model, new ChooseGoal(DesktopGoal.Stopped));
        var pending = DesktopMachine.Step(requested.Model, new CommandFinished(stop.Command!.Id,
            Acknowledgement.Accepted, Host(2) with { Service = ServiceState.StopPending }));
        Assert.Equal(stop.Command, pending.Model.InFlight);
        Assert.True(pending.Model.AwaitingConfirmation);
        Assert.Null(pending.Command);
        var logoff = DesktopMachine.Step(pending.Model, new Observe(Host(3) with
            { Service = ServiceState.Stopped, Streamer = StreamerState.Absent }));
        Assert.Equal(CommandKind.LogoffChild, logoff.Command!.Kind);
        var start = Finish(logoff, Host(4) with { ChildSession = null,
            Service = ServiceState.Stopped, Streamer = StreamerState.Absent });
        Assert.Equal(CommandKind.StartService, start.Command!.Kind);
        Assert.True(Finish(start, Host(5) with { ChildSession = null }).Model.Quiescent);
    }

    [Fact]
    public void FailedStartRetainsResponsibilityUntilExplicitRetrySucceeds()
    {
        var start = DesktopMachine.Step(With(Host() with { Service = ServiceState.Stopped }), new RepairService());
        var rejected = DesktopMachine.Step(start.Model,
            new CommandFinished(start.Command!.Id, Acknowledgement.Rejected));
        Assert.False(rejected.Model.Quiescent);
        Assert.Equal(RecoveryStage.NeedStart, rejected.Model.Recovery);
        var retry = DesktopMachine.Step(rejected.Model, new Retry());
        Assert.Equal(CommandKind.StartService, retry.Command!.Kind);
        Assert.True(Finish(retry, Host(2)).Model.Quiescent);
    }

    [Fact]
    public void MissingStreamerDoesNotStartARecoveryUnlessWeTerminatedIt()
    {
        var offline = DesktopMachine.Step(With(Host() with { Streamer = StreamerState.Absent }),
            new ChooseGoal(DesktopGoal.Host));
        for (var i = 0; i < 10; i++) offline = DesktopMachine.Step(offline.Model, new Tick());
        Assert.Null(offline.Command);
        Assert.True(offline.Model.Quiescent);
    }

    [Fact]
    public void OwnedStreamerRecoveryUsesTheSameCommandSlotAndDoesNotSpin()
    {
        var recycle = DesktopMachine.Step(With(Host() with { Streamer = StreamerState.Child }),
            new ChooseGoal(DesktopGoal.Host));
        var missing = Finish(recycle, Host(2) with { Streamer = StreamerState.Absent });
        Assert.Null(missing.Command);
        for (var i = 0; i < 2; i++)
        {
            missing = DesktopMachine.Step(missing.Model, new Tick());
            Assert.Null(missing.Command);
        }
        var stop = DesktopMachine.Step(missing.Model, new Tick());
        Assert.Equal(CommandKind.StopService, stop.Command!.Kind);
        Assert.Null(DesktopMachine.Step(stop.Model, new Tick()).Command);
    }

    [Fact]
    public void PlannerHonorsCommandPreconditionsAcross3000ObservationCombinations()
    {
        var checkedStates = 0;
        foreach (var goal in Enum.GetValues<DesktopGoal>())
        foreach (var rdp in Enum.GetValues<RdpState>())
        foreach (var service in Enum.GetValues<ServiceState>())
        foreach (var streamer in Enum.GetValues<StreamerState>())
        foreach (var child in new int?[] { null, 7 })
        foreach (var active in new bool?[] { null, false, true })
        {
            var facts = new DesktopObservation(1, child, active, rdp, service, streamer);
            var decision = DesktopMachine.Step(With(facts), new ChooseGoal(goal));
            checkedStates++;
            if (decision.Command is not { } command) continue;
            Assert.Equal(command, decision.Model.InFlight);
            switch (command.Kind)
            {
                case CommandKind.LogoffChild:
                    Assert.Equal(DesktopGoal.Stopped, goal);
                    Assert.Equal(RdpState.Disconnected, rdp);
                    Assert.NotNull(command.Session);
                    Assert.NotEqual(true, active);
                    break;
                case CommandKind.ConnectChild:
                    Assert.Equal(DesktopGoal.Child, goal);
                    Assert.True(child is not null || command.AllowCreate);
                    break;
                case CommandKind.RecycleStreamer:
                    Assert.Equal(ServiceState.Running, service);
                    Assert.Equal(goal == DesktopGoal.Child ? RdpState.Connected : RdpState.Disconnected, rdp);
                    Assert.Equal(goal == DesktopGoal.Child, active == true);
                    break;
            }
        }
        Assert.Equal(3000, checkedStates);
    }

    private static DesktopDecision Finish(DesktopDecision decision, DesktopObservation observation) =>
        DesktopMachine.Step(decision.Model,
            new CommandFinished(decision.Command!.Id, Acknowledgement.Accepted, observation));
}
