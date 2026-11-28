using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>Waits without occupying a worker and rejects superseded verification before starting SFC.</summary>
internal static class SfcVerificationQueue
{
    internal static async Task<int> RunAsync(SemaphoreSlim gate, TimeSpan timeout,
        Func<bool> isCurrent, Func<int> verify, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent()) return -1;
        if (!await gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false)) return -1;
        try
        {
            // A newer request may arrive while this one waits behind verification or repair.
            cancellationToken.ThrowIfCancellationRequested();
            return isCurrent() ? verify() : -1;
        }
        finally { gate.Release(); }
    }
}
