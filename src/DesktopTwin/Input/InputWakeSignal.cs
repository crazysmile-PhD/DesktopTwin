using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DesktopTwin.Input;

/// <summary>Coalesces notifications for one reader; one reusable timer replaces per-wait timeout tasks.</summary>
internal sealed class InputWakeSignal : IDisposable
{
    // Only wake tokens coalesce; InputPacketQueue retains the ordered input transitions.
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
    private readonly Timer _timer;
    private bool _timerArmed;
    private int _disposed;

    internal InputWakeSignal() => _timer = new Timer(_ => Set(), null, Timeout.Infinite, Timeout.Infinite);

    internal void Set() => _wake.Writer.TryWrite(0);

    /// <summary>The single reader clears one old token before rechecking durable queue, lease and request state.</summary>
    internal bool ClearPending() => _wake.Reader.TryRead(out _);

    /// <summary>The single owner consumes each result once; its shutdown must signal the pending read.</summary>
    internal ValueTask<byte> WaitAsync(int milliseconds)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (milliseconds == 0) return new ValueTask<byte>(0);
        if (milliseconds != Timeout.Infinite || _timerArmed)
        {
            _timer.Change(milliseconds, Timeout.Infinite);
            _timerArmed = milliseconds != Timeout.Infinite;
        }
        return _wake.Reader.ReadAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        Set();
    }
}
