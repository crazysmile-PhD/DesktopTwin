namespace DesktopTwin.Experiments.UnifiedDesktop;

public enum DesktopGoal { Observe, Stopped, Host, Child }
public enum RdpState { Unknown, Disconnected, Connecting, Connected, Disconnecting }
public enum ServiceState { Unknown, Stopped, StartPending, StopPending, Running }
public enum StreamerState { Unknown, Absent, Host, Child, Other }
public enum RecoveryStage { None, NeedStop, AwaitStopped, NeedStart, AwaitRunning }
public enum CommandKind { DisconnectRdp, ConnectChild, LogoffChild, RecycleStreamer, StopService, StartService }
public enum Acknowledgement { Accepted, Rejected, Unknown }

// Revision is assigned by ONE observation adapter, not independently by three producers.
// A snapshot is a collection of observations, not an atomic Windows transaction.
public sealed record DesktopObservation(long Revision, int? ChildSession, bool? ChildActive,
    RdpState Rdp, ServiceState Service, StreamerState Streamer)
{
    public static DesktopObservation Unknown { get; } = new(0, null, null,
        RdpState.Unknown, ServiceState.Unknown, StreamerState.Unknown);
}

public sealed record DesktopCommand(long Id, long Intent, CommandKind Kind, int? Session = null,
    bool AllowCreate = false, long ObservationRevision = 0);

public sealed record DesktopModel
{
    public DesktopGoal Goal { get; init; } = DesktopGoal.Observe;
    public long Intent { get; init; }
    public long NextCommand { get; init; } = 1;
    public bool CreationAdmission { get; init; }
    public RecoveryStage Recovery { get; init; }
    public bool OwnsStreamerRecovery { get; init; }
    public int WaitingTicks { get; init; }
    public DesktopObservation Observed { get; init; } = DesktopObservation.Unknown;
    public DesktopCommand? InFlight { get; init; }
    public bool AwaitingConfirmation { get; init; }
    public string? Fault { get; init; }
    public bool Quiescent => InFlight is null && Recovery == RecoveryStage.None && !OwnsStreamerRecovery;
}

public abstract record DesktopEvent;
public sealed record ChooseGoal(DesktopGoal Goal) : DesktopEvent;
public sealed record Observe(DesktopObservation Value) : DesktopEvent;
public sealed record RepairService : DesktopEvent;
public sealed record Retry : DesktopEvent;
public sealed record Tick : DesktopEvent;
public sealed record CommandFinished(long Id, Acknowledgement Outcome,
    DesktopObservation? Observation = null, string? Error = null) : DesktopEvent;
public sealed record DesktopDecision(DesktopModel Model, DesktopCommand? Command = null);

/// <summary>
/// Pure, single-owner reducer. Adapters report facts and execute one command; they never decide retries,
/// resurrect sessions, or call each other. This method never awaits an adapter or acquires a lock.
/// </summary>
public static class DesktopMachine
{
    public static DesktopDecision Step(DesktopModel model, DesktopEvent input)
    {
        switch (input)
        {
            case ChooseGoal goal:
                model = model with { Goal = goal.Goal, Intent = model.Intent + 1,
                    CreationAdmission = goal.Goal == DesktopGoal.Child, Fault = null };
                break;
            case Observe observation:
                model = AcceptObservation(model, observation.Value);
                if (model.AwaitingConfirmation && model.InFlight is { } awaiting
                    && IsConfirmed(awaiting, model.Observed))
                    return Step(model, new CommandFinished(awaiting.Id, Acknowledgement.Accepted));
                break;
            case RepairService:
                model = model with { Fault = null, Recovery = model.Recovery == RecoveryStage.None
                    ? RecoveryStage.NeedStop : model.Recovery };
                break;
            case Retry:
                model = model with { Fault = null };
                break;
            case Tick:
                model = model with { WaitingTicks = Math.Min(3, model.WaitingTicks + 1) };
                break;
            case CommandFinished finished:
                // Duplicate/late acknowledgements cannot complete another command or refresh old facts.
                if (model.InFlight is not { } active || active.Id != finished.Id) return new(model);
                if (finished.Observation is { } facts) model = AcceptObservation(model, facts);
                if (active.Kind == CommandKind.StopService)
                    model = model with { Recovery = finished.Outcome == Acknowledgement.Rejected
                        ? RecoveryStage.None : RecoveryStage.AwaitStopped };
                else if (active.Kind == CommandKind.StartService)
                    model = model with { Recovery = finished.Outcome == Acknowledgement.Rejected
                        ? RecoveryStage.NeedStart : RecoveryStage.AwaitRunning };
                if (finished.Outcome != Acknowledgement.Rejected && !IsConfirmed(active, model.Observed))
                {
                    // The native request may have been accepted without settling. Keep the SAME command
                    // slot occupied until a fresh observation or an explicit terminal callback confirms it.
                    return new(model with { AwaitingConfirmation = true, Fault = finished.Error });
                }
                model = model with { InFlight = null, AwaitingConfirmation = false, WaitingTicks = 0,
                    Fault = model.AwaitingConfirmation ? null : model.Fault };
                if (active.Kind == CommandKind.RecycleStreamer
                    && finished.Outcome == Acknowledgement.Rejected)
                    model = model with { OwnsStreamerRecovery = false };

                if (finished.Outcome == Acknowledgement.Rejected && active.Intent == model.Intent)
                {
                    if (active.Kind == CommandKind.ConnectChild && active.AllowCreate)
                        model = model with { CreationAdmission = true };
                    model = model with { Fault = finished.Error ?? $"{active.Kind} rejected" };
                }
                break;
        }
        return Plan(model);
    }

    private static DesktopModel AcceptObservation(DesktopModel model, DesktopObservation value) =>
        value.Revision > model.Observed.Revision ? model with { Observed = value } : model;

    private static bool IsConfirmed(DesktopCommand command, DesktopObservation seen) =>
        seen.Revision > command.ObservationRevision && command.Kind switch
        {
            CommandKind.DisconnectRdp => seen.Rdp == RdpState.Disconnected && seen.ChildActive != true,
            CommandKind.ConnectChild => seen.ChildSession is not null && seen.ChildActive == true
                && seen.Rdp == RdpState.Connected
                && (command.AllowCreate || command.Session == seen.ChildSession),
            CommandKind.LogoffChild => seen.ChildSession != command.Session,
            CommandKind.RecycleStreamer => seen.Streamer is StreamerState.Absent
                || (seen.Streamer == StreamerState.Child && seen.ChildActive == true && seen.Rdp == RdpState.Connected)
                || (seen.Streamer == StreamerState.Host && seen.ChildActive != true && seen.Rdp == RdpState.Disconnected),
            CommandKind.StopService => seen.Service == ServiceState.Stopped,
            CommandKind.StartService => seen.Service == ServiceState.Running,
            _ => false
        };

    private static DesktopDecision Plan(DesktopModel model)
    {
        if (model.InFlight is not null || model.Fault is not null) return new(model);
        var seen = model.Observed;

        // After the active command settles, logoff has priority over starting another transition.
        if (model.Goal == DesktopGoal.Stopped)
        {
            if (seen.Rdp == RdpState.Unknown) return new(model);
            if (seen.Rdp != RdpState.Disconnected || seen.ChildActive == true)
                return Issue(model, CommandKind.DisconnectRdp, seen.ChildSession);
            if (seen.ChildSession is { } child) return Issue(model, CommandKind.LogoffChild, child);
        }

        switch (model.Recovery)
        {
            case RecoveryStage.AwaitStopped:
                // RUNNING immediately after accepted stop is not evidence that the stop was withdrawn.
                if (seen.Service != ServiceState.Stopped) return new(model);
                model = model with { Recovery = RecoveryStage.NeedStart };
                goto case RecoveryStage.NeedStart;
            case RecoveryStage.NeedStart:
                if (seen.Service == ServiceState.Running)
                    model = model with { Recovery = RecoveryStage.None };
                else if (seen.Service == ServiceState.Stopped) return Issue(model, CommandKind.StartService);
                else return new(model);
                break;
            case RecoveryStage.AwaitRunning:
                if (seen.Service != ServiceState.Running) return new(model);
                model = model with { Recovery = RecoveryStage.None };
                break;
            case RecoveryStage.NeedStop:
                if (seen.Service == ServiceState.Running) return Issue(model, CommandKind.StopService);
                if (seen.Service == ServiceState.Stopped)
                    return Issue(model with { Recovery = RecoveryStage.NeedStart }, CommandKind.StartService);
                return new(model);
        }

        if (model.Goal is DesktopGoal.Observe or DesktopGoal.Stopped)
            return RecoverOwnedStreamer(model);
        if (seen.Rdp == RdpState.Unknown) return new(model);
        if (model.Goal == DesktopGoal.Host)
        {
            // The experiment removes keep_child_connected: HOST means RDP stays disconnected.
            if (seen.Rdp != RdpState.Disconnected || seen.ChildActive == true)
                return Issue(model, CommandKind.DisconnectRdp, seen.ChildSession);
            if (seen.ChildSession is not null && seen.ChildActive is null) return new(model);
        }
        else
        {
            if (seen.ChildSession is null)
                return model.CreationAdmission
                    ? Issue(model with { CreationAdmission = false }, CommandKind.ConnectChild, allowCreate: true)
                    : new(model); // Observation/lock notifications never grant creation permission.
            // An existing session consumes explicit-start permission, too. Otherwise a later deletion
            // would turn a settled lock target into an unintended automatic creation request.
            model = model with { CreationAdmission = false };
            if (seen.Rdp == RdpState.Disconnected)
                return Issue(model, CommandKind.ConnectChild, seen.ChildSession);
            if (seen.Rdp != RdpState.Connected || seen.ChildActive != true) return new(model);
        }

        var expected = model.Goal == DesktopGoal.Child ? StreamerState.Child : StreamerState.Host;
        if (seen.Streamer == expected)
            return new(model with { OwnsStreamerRecovery = false, WaitingTicks = 0 });
        if (model.OwnsStreamerRecovery) return RecoverOwnedStreamer(model);
        if (seen.Streamer is StreamerState.Absent or StreamerState.Unknown) return new(model);
        if (seen.Service != ServiceState.Running) return new(model);
        return Issue(model with { OwnsStreamerRecovery = true, WaitingTicks = 0 }, CommandKind.RecycleStreamer);
    }

    private static DesktopDecision RecoverOwnedStreamer(DesktopModel model)
    {
        if (!model.OwnsStreamerRecovery) return new(model);
        var streamer = model.Observed.Streamer;
        var expected = model.Goal == DesktopGoal.Child ? StreamerState.Child : StreamerState.Host;
        if (streamer == expected || (model.Goal == DesktopGoal.Observe
            && streamer is StreamerState.Host or StreamerState.Child))
            return new(model with { OwnsStreamerRecovery = false, WaitingTicks = 0 });
        if (model.WaitingTicks < 3) return new(model);
        return model.Observed.Service switch
        {
            ServiceState.Running => Issue(model with { Recovery = RecoveryStage.NeedStop }, CommandKind.StopService),
            ServiceState.Stopped => Issue(model with { Recovery = RecoveryStage.NeedStart }, CommandKind.StartService),
            _ => new(model)
        };
    }

    private static DesktopDecision Issue(DesktopModel model, CommandKind kind, int? session = null,
        bool allowCreate = false)
    {
        var command = new DesktopCommand(model.NextCommand, model.Intent, kind, session, allowCreate,
            model.Observed.Revision);
        return new(model with { InFlight = command, NextCommand = command.Id + 1 }, command);
    }
}
