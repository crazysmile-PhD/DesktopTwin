using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopTwin.Contracts;

namespace DesktopTwin.View;

/// <summary>Dispatches session work without exposing WPF to the lifecycle owner.</summary>
internal sealed class WpfSessionDispatcher(Dispatcher dispatcher) : ISessionDispatcher
{
    /// <inheritdoc />
    public bool CheckAccess() => dispatcher.CheckAccess();
    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> action) => CheckAccess()
        ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();
    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        if (!CheckAccess()) return dispatcher.InvokeAsync(action).Task;
        action();
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public void Post(Action action)
    {
        if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
