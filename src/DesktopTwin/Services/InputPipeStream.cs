using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Microsoft.Win32.SafeHandles;

namespace DesktopTwin.Services;

/// <summary>Serial native pipe IO on a pool worker; its ValueTask resumes there without another enqueue.</summary>
internal sealed class InputPipeStream : Stream, IValueTaskSource<int>, IValueTaskSource
{
    private readonly object _gate = new();
    private readonly ManualResetEvent _completion = new(false);
    private readonly byte[] _request, _status;
    private readonly CancellationToken _stop;
    private readonly SafePipeHandle _pipeHandle;
    private static readonly NativeWaitCallback Callback = OnCompleted;
    [ThreadStatic] private static bool _completingIo;
    private CancellationTokenRegistration _cancellation;
    private ManualResetValueTaskSourceCore<int> _source; // Continuations may run on the native IO worker.
    private GCHandle _requestPin, _statusPin, _context;
    private IntPtr _handle, _overlapped, _wait, _requestAddress, _statusAddress;
    private bool _handleOwned, _disposed, _inOperation, _awaitingResult, _writing;
    private int _length;

    internal InputPipeStream(PipeStream pipe, byte[] request, byte[] status, CancellationToken stop,
        Func<IntPtr, IntPtr, IntPtr>? createWait = null)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(status);
        _request = request; _status = status; _stop = stop;
        _pipeHandle = pipe.SafePipeHandle;
        try
        {
            if (!pipe.IsConnected || !pipe.IsAsync) throw new ArgumentException("A connected overlapped pipe is required.", nameof(pipe));
            _pipeHandle.DangerousAddRef(ref _handleOwned);
            _handle = _pipeHandle.DangerousGetHandle();
            _requestPin = GCHandle.Alloc(request, GCHandleType.Pinned);
            _statusPin = GCHandle.Alloc(status, GCHandleType.Pinned);
            _requestAddress = _requestPin.AddrOfPinnedObject();
            _statusAddress = _statusPin.AddrOfPinnedObject();
            _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedData>());
            Marshal.StructureToPtr(new OverlappedData
            {
                // Suppress the existing PipeStream IOCP binding; only our event reports this operation.
                Event = new IntPtr(_completion.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1)
            }, _overlapped, false);
            _context = GCHandle.Alloc(this, GCHandleType.Weak);
            _wait = (createWait ?? CreateWait)(Marshal.GetFunctionPointerForDelegate(Callback), GCHandle.ToIntPtr(_context));
            if (_wait == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            // The transport token is permanent after timeout/stop; no per-operation registration is needed.
            _cancellation = stop.UnsafeRegister(CancelCallback, this);
        }
        catch { Dispose(false); throw; }
    }

    ~InputPipeStream() { Dispose(false); }
    // Event-based IO is canceled when its issuing thread exits. The owner moves arbitrary callers
    // to the managed pool; native completion workers can continue the same serial chain directly.
    internal static bool IsIoWorker => _completingIo || Thread.CurrentThread.IsThreadPoolThread;
    internal bool IsIoPending { get { lock (_gate) return _inOperation; } }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var pending = Begin(buffer, false, cancellationToken, out var bytes, out var version);
        return pending ? new ValueTask<int>(this, version) : ValueTask.FromResult(bytes);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var pending = Begin(buffer, true, cancellationToken, out _, out var version);
        return pending ? new ValueTask(this, version) : ValueTask.CompletedTask;
    }

    private bool Begin(ReadOnlyMemory<byte> buffer, bool writing, CancellationToken token, out int bytes, out short version)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inOperation || _awaitingResult) throw new InvalidOperationException("Input IO must be serialized and consumed once.");
            if (token.CanBeCanceled && token != _stop) throw new ArgumentException("IO must use the transport token.", nameof(token));
            _stop.ThrowIfCancellationRequested();
            if (!MemoryMarshal.TryGetArray(buffer, out var segment) || segment.Array != (writing ? _request : _status))
                throw new ArgumentException("IO requires transport-owned buffers.", nameof(buffer));
            bytes = 0; version = 0;
            if (buffer.Length == 0) return false;
            _writing = writing; _length = buffer.Length;
            _inOperation = true;
            Marshal.WriteIntPtr(_overlapped, 0, IntPtr.Zero);
            Marshal.WriteIntPtr(_overlapped, IntPtr.Size, IntPtr.Zero);
            var address = IntPtr.Add(writing ? _requestAddress : _statusAddress, segment.Offset);
            var success = writing ? WriteFile(_handle, address, _length, IntPtr.Zero, _overlapped)
                : ReadFile(_handle, address, _length, IntPtr.Zero, _overlapped);
            var error = success ? 0 : Marshal.GetLastWin32Error();
            if (error == 997)
            {
                _source.Reset();
                _awaitingResult = true;
                version = _source.Version;
                SetThreadpoolWait(_wait, _completion.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero);
                return true;
            }
            _inOperation = false;
            if (error == 0 && !GetOverlappedResult(_handle, _overlapped, out bytes, false)) error = Marshal.GetLastWin32Error();
            bytes = Result(bytes, error);
            return false;
        }
    }

    private int Result(int bytes, int error)
    {
        if (error == 995 && _stop.IsCancellationRequested) throw new OperationCanceledException(_stop);
        if (!_writing && (error == 109 || error == 232 || error == 233)) return 0;
        if (error != 0) throw new IOException("Input pipe IO failed.", new Win32Exception(error));
        if (_writing) ValidateWrite(bytes, _length);
        return bytes;
    }

    /// <summary>A partial control frame ends the channel rather than reporting an incomplete write as success.</summary>
    internal static void ValidateWrite(int bytes, int expected)
    {
        if (bytes != expected) throw new IOException("Input pipe write did not complete its frame.");
    }

    private void CompletePending(bool signal)
    {
        int bytes;
        Exception? failure = null;
        lock (_gate)
        {
            if (!_inOperation) return;
            var error = GetOverlappedResult(_handle, _overlapped, out bytes, false) ? 0 : Marshal.GetLastWin32Error();
            _inOperation = false;
            if (signal)
            {
                try { bytes = Result(bytes, error); }
                catch (Exception errorResult) when (errorResult is IOException or OperationCanceledException) { failure = errorResult; }
            }
        }
        // SetResult can resume the pump, consume this result and start the next IO inline.
        // All native state was inspected above; nothing may touch that operation after signaling.
        if (!signal) return;
        if (failure is null) _source.SetResult(bytes);
        else _source.SetException(failure);
    }

    private void CancelPending()
    {
        lock (_gate) { if (_inOperation) CancelIoEx(_handle, _overlapped); }
    }

    private static void CancelCallback(object? state) { if (state is InputPipeStream owner) owner.CancelPending(); }

    protected override void Dispose(bool disposing)
    {
        bool pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _inOperation;
            if (pending) CancelIoEx(_handle, _overlapped);
        }
        _cancellation.Dispose();
        if (pending) { _completion.WaitOne(); CompletePending(disposing); }
        if (_wait != IntPtr.Zero)
        {
            SetThreadpoolWait(_wait, IntPtr.Zero, IntPtr.Zero);
            WaitForThreadpoolWaitCallbacks(_wait, true);
            CloseThreadpoolWait(_wait);
            _wait = IntPtr.Zero;
        }
        if (_context.IsAllocated) _context.Free();
        if (_overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_overlapped); _overlapped = IntPtr.Zero; }
        if (_requestPin.IsAllocated) _requestPin.Free();
        if (_statusPin.IsAllocated) _statusPin.Free();
        if (_handleOwned) { _pipeHandle.DangerousRelease(); _handleOwned = false; }
        _completion.Dispose();
        base.Dispose(disposing);
    }

    private static IntPtr CreateWait(IntPtr callback, IntPtr context) => CreateThreadpoolWait(callback, context, IntPtr.Zero);
    private static void OnCompleted(IntPtr instance, IntPtr context, IntPtr wait, uint result)
    {
        if (GCHandle.FromIntPtr(context).Target is not InputPipeStream owner) return;
        // An inline continuation may dispose the wait itself; context is no longer used below.
        DisassociateCurrentThreadFromCallback(instance);
        var wasCompleting = _completingIo;
        _completingIo = true;
        try { owner.CompletePending(true); }
        finally { _completingIo = wasCompleting; }
    }

    private int Consume(short token)
    {
        lock (_gate)
        {
            if (!_awaitingResult || _source.GetStatus(token) == ValueTaskSourceStatus.Pending)
                throw new InvalidOperationException("Input IO result is not ready or was already consumed.");
            try { return _source.GetResult(token); }
            finally { _awaitingResult = false; }
        }
    }
    int IValueTaskSource<int>.GetResult(short token) => Consume(token);
    void IValueTaskSource.GetResult(short token) => Consume(token);
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _source.GetStatus(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _source.GetStatus(token);
    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);

    public override bool CanRead => !_disposed;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedData { internal IntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NativeWaitCallback(IntPtr instance, IntPtr context, IntPtr wait, uint result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateThreadpoolWait(IntPtr callback, IntPtr context, IntPtr environment);
    [DllImport("kernel32.dll")]
    private static extern void SetThreadpoolWait(IntPtr wait, IntPtr handle, IntPtr timeout);
    [DllImport("kernel32.dll")]
    private static extern void WaitForThreadpoolWaitCallbacks(IntPtr wait, [MarshalAs(UnmanagedType.Bool)] bool cancelPending);
    [DllImport("kernel32.dll")]
    private static extern void CloseThreadpoolWait(IntPtr wait);
    [DllImport("kernel32.dll")]
    private static extern void DisassociateCurrentThreadFromCallback(IntPtr instance);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, IntPtr bytes, int count, IntPtr read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, IntPtr bytes, int count, IntPtr written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out int transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
}
