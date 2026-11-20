using System;
using System.Collections.Generic;

namespace DesktopTwin.Input;

/// <summary>Coalesces adjacent movement only; button/key ordering is never changed or silently dropped.</summary>
internal sealed class InputPacketQueue
{
    private readonly object _gate = new();
    private readonly LinkedList<InputPacket> _pending = new();
    internal bool TryAdd(InputPacket packet)
    {
        lock (_gate)
        {
            packet.Validate();
            if (packet.Operation == InputOperation.Move && _pending.Last is { Value.Operation: InputOperation.Move } last)
            {
                var x = (long)last.Value.X + packet.X;
                var y = (long)last.Value.Y + packet.Y;
                if (Math.Abs(x) > 1000000 || Math.Abs(y) > 1000000) return false;
                last.Value = new InputPacket(InputOperation.Move, (int)x, (int)y);
                return true;
            }
            if (_pending.Count >= 128) return false;
            _pending.AddLast(packet);
            return true;
        }
    }
    internal bool TryTake(out InputPacket packet)
    {
        lock (_gate)
        {
            if (_pending.First is not { } first) { packet = default; return false; }
            packet = first.Value;
            _pending.RemoveFirst();
            return true;
        }
    }
    internal void Clear() { lock (_gate) _pending.Clear(); }
}
