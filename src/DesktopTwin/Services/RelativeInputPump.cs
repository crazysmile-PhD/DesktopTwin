using System;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Input;

namespace DesktopTwin.Services;

/// <summary>One serialized exchange owner prefers queued input replies over redundant status heartbeats.</summary>
internal sealed class RelativeInputPump(RelativeInputTransport transport, InputPacketQueue queue, InputWakeSignal signal,
    Func<long> leaseUntil, Func<bool> takeStatusRequest, Action<RelativeInputStatus> publish,
    Func<long>? milliseconds = null)
{
    private const int HeartbeatMilliseconds = 50;
    private readonly Func<long> _now = milliseconds ?? (static () => Environment.TickCount64);

    internal async Task RunAsync(uint sessionId, CancellationToken stop)
    {
        var armed = false;
        publish(await transport.ExchangeAsync(new(InputOperation.Heartbeat, 0), sessionId).ConfigureAwait(false));
        var exchangedAt = _now();
        while (!stop.IsCancellationRequested)
        {
            // Every producer publishes durable state before Set. Clear first, then recheck all state;
            // a racing producer either becomes visible below or leaves a new token for WaitAsync.
            signal.ClearPending();
            var allowed = _now() < leaseUntil();
            var requested = takeStatusRequest();
            var exchanged = false;
            if (allowed != armed)
            {
                publish(await transport.ExchangeAsync(new(InputOperation.Heartbeat, allowed ? 1 : 0), sessionId).ConfigureAwait(false));
                armed = allowed;
                exchangedAt = _now();
                exchanged = true;
            }
            // Keep transitions in the queue throughout authorization IO, so a revocation can
            // invalidate them even if a new grant arrives before the old reply completes.
            if (!armed)
            {
                if (_now() < leaseUntil()) continue;
                queue.Clear();
            }
            else
            {
                while (!stop.IsCancellationRequested && queue.TryTake(out var packet))
                {
                    if (_now() >= leaseUntil())
                    {
                        queue.Clear();
                        publish(await transport.ExchangeAsync(new(InputOperation.Heartbeat, 0), sessionId).ConfigureAwait(false));
                        armed = false;
                        exchangedAt = _now();
                        exchanged = true;
                        break;
                    }
                    // A request published before this write is fulfilled by this packet's fresh reply.
                    // Requests arriving during the exchange remain pending for the next turn.
                    takeStatusRequest();
                    publish(await transport.ExchangeAsync(packet, sessionId).ConfigureAwait(false));
                    exchangedAt = _now();
                    exchanged = true;
                }
            }
            if (!exchanged && (requested || (armed && _now() - exchangedAt >= HeartbeatMilliseconds)))
            {
                publish(await transport.ExchangeAsync(new(InputOperation.Heartbeat, armed ? 1 : 0), sessionId).ConfigureAwait(false));
                exchangedAt = _now();
                exchanged = true;
            }
            if (exchanged) continue;
            if (stop.IsCancellationRequested) break;
            var wait = armed ? (int)Math.Clamp(HeartbeatMilliseconds - (_now() - exchangedAt), 0, HeartbeatMilliseconds)
                : Timeout.Infinite;
            await signal.WaitAsync(wait).ConfigureAwait(false);
        }
    }
}
