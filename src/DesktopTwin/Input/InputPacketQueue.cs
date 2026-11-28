using System;

namespace DesktopTwin.Input;

/// <summary>Coalesces adjacent movement only; button/key ordering is never changed or silently dropped.</summary>
internal sealed class InputPacketQueue
{
    private readonly object _gate = new();
    private const int Capacity = 128;
    private readonly InputPacket[] _pending = new InputPacket[Capacity];
    private int _head;
    private int _count;
    internal bool TryAdd(InputPacket packet)
    {
        lock (_gate)
        {
            var last = (_head + _count - 1 + Capacity) % Capacity;
            if (packet.Operation == InputOperation.Move && _count != 0 && _pending[last].Operation == InputOperation.Move)
            {
                var x = (long)_pending[last].X + packet.X;
                var y = (long)_pending[last].Y + packet.Y;
                if (Math.Abs(x) > 1000000 || Math.Abs(y) > 1000000) return false;
                _pending[last] = new InputPacket(InputOperation.Move, (int)x, (int)y);
                return true;
            }
            if (_count == Capacity) return false;
            _pending[(_head + _count) % Capacity] = packet;
            _count++;
            return true;
        }
    }
    internal bool TryTake(out InputPacket packet)
    {
        lock (_gate)
        {
            if (_count == 0) { packet = default; return false; }
            packet = _pending[_head];
            _head = (_head + 1) % Capacity;
            _count--;
            return true;
        }
    }
    // Packets contain no references; clearing indices is sufficient even when the ring wrapped.
    internal void Clear() { lock (_gate) { _head = 0; _count = 0; } }
}
