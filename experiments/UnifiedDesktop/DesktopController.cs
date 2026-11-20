using System.Threading.Channels;

namespace DesktopTwin.Experiments.UnifiedDesktop;

public sealed record CommandResult(Acknowledgement Outcome, DesktopObservation? Observation = null,
    string? Error = null);

public interface IDesktopPlatform
{
    // Execute only this command. Report acceptance/actual facts; do not call another adapter or retry.
    Task<CommandResult> ExecuteAsync(DesktopCommand command);
}

/// <summary>One mailbox owns every decision. Device I/O runs outside the reader and reports a message.</summary>
public sealed class DesktopController : IAsyncDisposable
{
    private sealed record Envelope(DesktopEvent? Input, TaskCompletionSource<DesktopModel>? Reply, bool Close = false);
    private readonly Channel<Envelope> _mailbox = Channel.CreateUnbounded<Envelope>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly IDesktopPlatform _platform;
    private readonly Task _reader;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;
    private DesktopModel _model = new();

    public DesktopController(IDesktopPlatform platform)
    {
        _platform = platform;
        _reader = RunAsync();
    }

    // Cancellation cancels the caller's WAIT. Once admitted, intent survives until explicitly replaced.
    public Task<DesktopModel> PostAsync(DesktopEvent input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = new TaskCompletionSource<DesktopModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_mailbox.Writer.TryWrite(new(input, reply)))
            return Task.FromException<DesktopModel>(new ObjectDisposedException(nameof(DesktopController)));
        return reply.Task.WaitAsync(cancellationToken);
    }

    public Task<DesktopModel> InspectAsync() => AskAsync();

    private Task<DesktopModel> AskAsync()
    {
        var reply = new TaskCompletionSource<DesktopModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_mailbox.Writer.TryWrite(new(null, reply)))
            return Task.FromException<DesktopModel>(new ObjectDisposedException(nameof(DesktopController)));
        return reply.Task;
    }

    private async Task RunAsync()
    {
        await foreach (var message in _mailbox.Reader.ReadAllAsync())
        {
            if (message.Close) _closing = true;
            if (_closing && message.Input is ChooseGoal or RepairService)
            {
                message.Reply?.TrySetException(new ObjectDisposedException(nameof(DesktopController)));
                continue;
            }
            var decision = message.Close
                ? DesktopMachine.Step(_model, new ChooseGoal(DesktopGoal.Observe))
                : message.Input is { } input ? DesktopMachine.Step(_model, input) : new DesktopDecision(_model);
            _model = decision.Model;
            message.Reply?.TrySetResult(_model);
            if (decision.Command is { } command) _ = Task.Run(() => DispatchAsync(command));
            // Closing must not discard an accepted stop/start or in-flight command.
            if (_closing && _model.Quiescent)
            {
                _mailbox.Writer.TryComplete();
                // Resolve messages already queued at the close boundary instead of stranding their callers.
                while (_mailbox.Reader.TryRead(out var queued))
                    queued.Reply?.TrySetException(new ObjectDisposedException(nameof(DesktopController)));
                _closed.TrySetResult();
                return;
            }
        }
    }

    private async Task DispatchAsync(DesktopCommand command)
    {
        CommandResult result;
        try { result = await _platform.ExecuteAsync(command).ConfigureAwait(false); }
        catch (Exception exception)
        {
            // An adapter throwing cannot prove that a system command was NOT accepted.
            result = new(Acknowledgement.Unknown, Error: exception.Message);
        }
        _mailbox.Writer.TryWrite(new(new CommandFinished(command.Id, result.Outcome,
            result.Observation, result.Error), null));
    }

    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryWrite(new(null, null, Close: true));
        await _closed.Task.ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
    }
}
