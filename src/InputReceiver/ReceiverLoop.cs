using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using DesktopTwin.Input;

namespace DesktopTwinInputReceiver
{
    /// <summary>The IO thread owns the lease, including all releases after timeout, EOF or malformed input.</summary>
    internal static class ReceiverLoop
    {
        internal static void Run(ReceiverPipeStream pipe, int session, InputLease lease)
        {
            try
            {
                using (var watchdog = new LeaseWatchdog(pipe))
                {
                    while (true)
                    {
                        var packet = RelativeInputProtocol.ReadPacket(pipe, pipe.RequestBytes);
                        var receivedAt = Stopwatch.GetTimestamp();
                        lease.Apply(packet);
                        watchdog.Renew(lease.Armed, receivedAt);
                        ReceiverInput.WriteStatus(pipe.StatusBytes, session, lease);
                        pipe.Write(pipe.StatusBytes, 0, pipe.StatusBytes.Length);
                        if (packet.Operation == InputOperation.Close) return;
                    }
                }
            }
            finally
            {
                try { lease.Reset(); }
                catch (Win32Exception) { } // The input desktop may already have ended during session shutdown.
            }
        }

        private sealed class LeaseWatchdog : IDisposable
        {
            private readonly ReceiverPipeStream _pipe;
            private readonly Timer _timer;
            private long _expiresAt = Stopwatch.GetTimestamp() + 20 * Stopwatch.Frequency;
            private bool _armed, _first = true;

            internal LeaseWatchdog(ReceiverPipeStream pipe)
            {
                _pipe = pipe;
                _timer = new Timer(_ => Observe(), null, 20000, Timeout.Infinite);
            }

            internal void Renew(bool armed, long receivedAt)
            {
                if (armed)
                {
                    Interlocked.Exchange(ref _expiresAt,
                        receivedAt + RelativeInputProtocol.LeaseMilliseconds * Stopwatch.Frequency / 1000);
                    if (!_armed) _timer.Change(RelativeInputProtocol.LeaseMilliseconds, Timeout.Infinite);
                }
                else if (_armed || _first)
                {
                    Interlocked.Exchange(ref _expiresAt, long.MaxValue);
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                _armed = armed;
                _first = false;
            }

            private void Observe()
            {
                var deadline = Interlocked.Read(ref _expiresAt);
                if (deadline == long.MaxValue) return;
                var remaining = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                if (remaining <= 0) { _pipe.Cancel(); return; }
                try { _timer.Change((int)remaining + 1, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }

            public void Dispose() { _timer.Dispose(); }
        }
    }
}
