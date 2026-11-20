using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace DesktopTwin.Input
{
    /// <summary>Owns only injected transitions; a revoked lease releases them exactly once.</summary>
    internal sealed class InputLease
    {
        private readonly Action<InputPacket> _inject;
        private readonly HashSet<int> _buttons = new HashSet<int>();
        private readonly HashSet<int> _keys = new HashSet<int>();
        internal bool Armed { get; private set; }
        internal int HeldCount { get { return _buttons.Count + _keys.Count; } }
        internal long Moves { get; private set; }
        internal InputLease(Action<InputPacket> inject) { _inject = inject; }
        internal void Apply(InputPacket packet)
        {
            packet.Validate();
            if (packet.Operation == InputOperation.Heartbeat)
            {
                if (packet.X == 0) Reset();
                Armed = packet.X == 1;
                return;
            }
            if (packet.Operation == InputOperation.Reset || packet.Operation == InputOperation.Close)
            { Reset(); return; }
            if (packet.Operation == InputOperation.ReleasePointer) { ReleasePointer(); return; }
            if (!Armed) return;
            if (packet.Operation == InputOperation.Move)
            { _inject(packet); Moves++; return; }
            if (packet.Operation == InputOperation.Wheel) { _inject(packet); return; }
            var held = packet.Operation == InputOperation.Button ? _buttons : _keys;
            if (packet.Y == 1)
            {
                if (!held.Contains(packet.X)) { _inject(packet); held.Add(packet.X); }
            }
            else if (held.Contains(packet.X)) { _inject(packet); held.Remove(packet.X); }
        }
        internal void Reset()
        {
            Armed = false;
            if (HeldCount == 0) return;
            var failures = new List<ExceptionDispatchInfo>();
            try { Release(_keys, InputOperation.Key); }
            catch (Exception error) { failures.Add(ExceptionDispatchInfo.Capture(error)); }
            try { ReleasePointer(); }
            catch (Exception error) { failures.Add(ExceptionDispatchInfo.Capture(error)); }
            if (failures.Count != 0) failures[0].Throw();
        }
        private void ReleasePointer()
        {
            Release(_buttons, InputOperation.Button);
        }
        private void Release(HashSet<int> held, InputOperation operation)
        {
            // Attempt every release even if one fails. Retain failures for a subsequent cleanup.
            if (held.Count == 0) return;
            var failures = new List<ExceptionDispatchInfo>();
            foreach (var value in new List<int>(held))
            {
                try { _inject(new InputPacket(operation, value, 0)); held.Remove(value); }
                catch (Exception error) { failures.Add(ExceptionDispatchInfo.Capture(error)); }
            }
            if (failures.Count != 0) failures[0].Throw();
        }
    }
}
