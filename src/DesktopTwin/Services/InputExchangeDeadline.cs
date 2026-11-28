using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopTwin.Services;

/// <summary>One cancellable native timer avoids empty managed timer-queue wakes after completed IO.</summary>
internal sealed class InputExchangeDeadline : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source;
    private readonly NativeTimer _timer;
    private readonly int _milliseconds;
    private long _expiresAt;
    private bool _expired, _disposed;
    internal CancellationToken Token => _source.Token;

    internal InputExchangeDeadline(CancellationToken stop, int milliseconds = 1000,
        Func<IntPtr, IntPtr, IntPtr>? createTimer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(milliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, 60000);
        _milliseconds = milliseconds;
        _source = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try { _timer = new NativeTimer(this, createTimer ?? CreateTimer); }
        catch { _source.Dispose(); throw; }
    }

    internal void Begin()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_expired) throw new OperationCanceledException(Token);
            Token.ThrowIfCancellationRequested();
            _expiresAt = Stopwatch.GetTimestamp() + _milliseconds * Stopwatch.Frequency / 1000;
            var due = -_milliseconds * 10000L;
            SetThreadpoolTimer(_timer, ref due, 0, 0);
        }
    }

    internal void End()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _expiresAt = 0;
            StopTimer(_timer, IntPtr.Zero, 0, 0);
        }
    }

    /// <summary>Rechecks a possibly stale native callback against the current exchange deadline.</summary>
    internal void Observe()
    {
        lock (_gate)
        {
            if (_disposed || _expiresAt == 0) return;
            var remaining = _expiresAt - Stopwatch.GetTimestamp();
            if (remaining > 0)
            {
                // A callback queued before a completed exchange must respect the current deadline.
                var due = -(remaining * 10000000 / Stopwatch.Frequency + 1);
                SetThreadpoolTimer(_timer, ref due, 0, 0);
                return;
            }
            _expiresAt = 0;
            _expired = true; // Expiration permanently ends this transport, even before Cancel runs.
        }
        try { _source.Cancel(); }
        catch (ObjectDisposedException) { } // IO completion can dispose the owner inline during cancellation.
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _expiresAt = 0;
        }
        _timer.Dispose();
        _source.Dispose();
    }

    private static IntPtr CreateTimer(IntPtr callback, IntPtr context)
        => CreateThreadpoolTimer(callback, context, IntPtr.Zero);

    private sealed class NativeTimer : SafeHandle
    {
        private static readonly NativeCallback Callback = Invoke;
        private GCHandle _context;
        internal NativeTimer(InputExchangeDeadline owner, Func<IntPtr, IntPtr, IntPtr> create) : base(IntPtr.Zero, true)
        {
            // The safe handle can finalize an abandoned owner; native context never roots the owner.
            _context = GCHandle.Alloc(owner, GCHandleType.Weak);
            try
            {
                SetHandle(create(Marshal.GetFunctionPointerForDelegate(Callback), GCHandle.ToIntPtr(_context)));
                if (IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch
            {
                _context.Free();
                throw;
            }
        }
        public override bool IsInvalid => handle == IntPtr.Zero;
        private static void Invoke(IntPtr instance, IntPtr context, IntPtr timer)
        {
            if (GCHandle.FromIntPtr(context).Target is not InputExchangeDeadline owner) return;
            // Cancellation may complete IO and dispose this timer on this same callback thread.
            // Context is no longer accessed after disassociation; owner remains a strong local.
            DisassociateCurrentThreadFromCallback(instance);
            owner.Observe();
        }
        protected override bool ReleaseHandle()
        {
            StopNativeTimer(handle, IntPtr.Zero, 0, 0);
            WaitForThreadpoolTimerCallbacks(handle, true);
            CloseThreadpoolTimer(handle);
            _context.Free();
            return true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NativeCallback(IntPtr instance, IntPtr context, IntPtr timer);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateThreadpoolTimer(IntPtr callback, IntPtr context, IntPtr environment);
    [DllImport("kernel32.dll")]
    private static extern void SetThreadpoolTimer(NativeTimer timer, ref long due, uint period, uint window);
    [DllImport("kernel32.dll", EntryPoint = "SetThreadpoolTimer")]
    private static extern void StopTimer(NativeTimer timer, IntPtr due, uint period, uint window);
    [DllImport("kernel32.dll", EntryPoint = "SetThreadpoolTimer")]
    private static extern void StopNativeTimer(IntPtr timer, IntPtr due, uint period, uint window);
    [DllImport("kernel32.dll")]
    private static extern void WaitForThreadpoolTimerCallbacks(IntPtr timer, [MarshalAs(UnmanagedType.Bool)] bool cancelPending);
    [DllImport("kernel32.dll")]
    private static extern void CloseThreadpoolTimer(IntPtr timer);
    [DllImport("kernel32.dll")]
    private static extern void DisassociateCurrentThreadFromCallback(IntPtr instance);
}
