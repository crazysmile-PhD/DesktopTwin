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
            var hasPacket = queue.TryTake(out var packet);
            var allowed = _now() < leaseUntil();
            var requested = takeStatusRequest();
            var exchanged = false;
            if (allowed != armed || ((!hasPacket || !allowed)
                && (requested || (armed && _now() - exchangedAt >= HeartbeatMilliseconds))))
            {
                publish(await transport.ExchangeAsync(new(InputOperation.Heartbeat, allowed ? 1 : 0), sessionId).ConfigureAwait(false));
                armed = allowed;
                exchangedAt = _now();
                exchanged = true;
            }
            while (hasPacket && !stop.IsCancellationRequested)
            {
                if (_now() >= leaseUntil())
                {
                    queue.Clear();
                    if (armed)
                    {
                        publish(await transport.ExchangeAsync(new(InputOperation.Heartbeat, 0), sessionId).ConfigureAwait(false));
                        armed = false;
                        exchangedAt = _now();
                        exchanged = true;
                    }
                    break;
                }
                // Authorization can change while a previous exchange is in flight. Grant first,
                // then recheck the short lease before sending this still-pending transition.
                if (!armed)
                {
                    publish(await transport.ExchangeAsync(new(InputOperation.Heartbeat, 1), sessionId).ConfigureAwait(false));
                    armed = true;
                    exchangedAt = _now();
                    exchanged = true;
                    continue;
                }
                // A request published before this write is fulfilled by this packet's fresh reply.
                // Requests arriving during the exchange remain pending for the next turn.
                takeStatusRequest();
                publish(await transport.ExchangeAsync(packet, sessionId).ConfigureAwait(false));
                exchangedAt = _now();
                exchanged = true;
                hasPacket = queue.TryTake(out packet);
            }
            if (exchanged) continue;
            if (stop.IsCancellationRequested) break;
            var wait = armed ? (int)Math.Clamp(HeartbeatMilliseconds - (_now() - exchangedAt), 0, HeartbeatMilliseconds)
                : Timeout.Infinite;
            await signal.WaitAsync(wait).ConfigureAwait(false);
        }
    }
}
