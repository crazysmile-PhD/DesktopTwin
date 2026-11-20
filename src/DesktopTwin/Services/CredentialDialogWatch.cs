using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>Owns queued and running watchers, including cancellation and the handoff to a new connection.</summary>
internal sealed class CredentialDialogWatch : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<Func<Task>, Task> _schedule;
    private CancellationTokenSource? _cancellation;
    private Task _completion = Task.CompletedTask;
    private bool _started;
    private bool _disposed;

    internal CredentialDialogWatch(Func<Func<Task>, Task>? schedule = null) => _schedule = schedule ?? Task.Run;
    internal bool IsStarted { get { lock (_sync) return _started; } }
    internal Task Completion { get { lock (_sync) return _completion; } }

    internal void Start(Func<CancellationToken, Task> watch)
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            var cancellation = new CancellationTokenSource();
            // Capture before queuing. Only the worker disposes this source, after all use ends.
            var token = cancellation.Token;
            var previous = _completion;
            _cancellation = cancellation;
            _started = true;
            _completion = _schedule(() => RunAsync(previous, cancellation, watch, token));
        }
    }

    internal void Stop()
    {
        lock (_sync)
        {
            _started = false;
            _cancellation?.Cancel();
            _cancellation = null;
        }
    }

    private async Task RunAsync(Task previous, CancellationTokenSource cancellation,
        Func<CancellationToken, Task> watch, CancellationToken token)
    {
        try
        {
            // Cancellation cannot interrupt a COM call already in progress. Drain it before the next generation.
            await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await watch(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { AppLog.Warn("自动登录：凭据监听已停止。", error); }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            Stop();
        }
    }
}
