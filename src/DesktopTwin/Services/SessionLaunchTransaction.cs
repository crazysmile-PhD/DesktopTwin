using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

internal static class SessionLaunchTransaction
{
    // The cleanup obligation begins before registration: an RPC failure may occur
    // after Windows accepted registration but before returning its response.
    internal static async Task ExecuteAsync(Func<Task> registerAndRun, Func<Task> connect,
        Func<Task> deleteAndVerify, Func<Task> launchAndConfirm, CancellationToken cancellationToken)
    {
        var deleted = false;
        Exception? operationError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registerAndRun().ConfigureAwait(false);
            await connect().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await deleteAndVerify().ConfigureAwait(false);
            deleted = true;
            cancellationToken.ThrowIfCancellationRequested();
            await launchAndConfirm().ConfigureAwait(false);
        }
        catch (Exception error) { operationError = error; throw; }
        finally
        {
            if (!deleted)
            {
                try { await deleteAndVerify().ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    throw new InvalidOperationException("临时启动任务清理未完成；目标启动未获授权。",
                        operationError is null ? cleanupError : new AggregateException(operationError, cleanupError));
                }
            }
        }
    }
}
