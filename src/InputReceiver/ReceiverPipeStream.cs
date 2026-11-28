using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using DesktopTwin.Input;

namespace DesktopTwinInputReceiver
{
    /// <summary>One serialized IO owner reuses pinned buffers, an event and OVERLAPPED; cancellation may run elsewhere.</summary>
    internal sealed class ReceiverPipeStream : Stream
    {
        private readonly object _gate = new object();
        private readonly NamedPipeClientStream _pipe;
        private readonly IntPtr _handle;
        private readonly ManualResetEvent _completion = new ManualResetEvent(false);
        internal readonly byte[] RequestBytes = new byte[RelativeInputProtocol.PacketBytes];
        internal readonly byte[] StatusBytes = new byte[RelativeInputProtocol.StatusBytes];
        private GCHandle _requestPin, _statusPin;
        private IntPtr _overlapped, _requestAddress, _statusAddress;
        private bool _stopped, _disposed, _inOperation, _disposeWaiting, _writing;
        private int _cancelError;

        internal ReceiverPipeStream(NamedPipeClientStream pipe)
        {
#if NET
            ArgumentNullException.ThrowIfNull(pipe);
            if (!pipe.IsConnected || !pipe.IsAsync) throw new ArgumentException("A connected overlapped pipe is required.", nameof(pipe));
#else
            // Framework and the C# 5 companion compiler have no modern ThrowIf/nameof helpers.
            if (pipe == null) throw new ArgumentNullException("pipe");
            if (!pipe.IsConnected || !pipe.IsAsync) throw new ArgumentException("A connected overlapped pipe is required.", "pipe");
#endif
            _pipe = pipe;
            _handle = pipe.SafePipeHandle.DangerousGetHandle();
            try
            {
                _requestPin = GCHandle.Alloc(RequestBytes, GCHandleType.Pinned);
                _statusPin = GCHandle.Alloc(StatusBytes, GCHandleType.Pinned);
                _requestAddress = _requestPin.AddrOfPinnedObject();
                _statusAddress = _statusPin.AddrOfPinnedObject();
                _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedData>());
                // The low bit suppresses IOCP callbacks on the Framework-bound pipe handle.
                // Only this event reports completion; no per-operation APM object is created.
                var data = new OverlappedData { Event = new IntPtr(_completion.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1) };
                Marshal.StructureToPtr(data, _overlapped, false);
            }
            catch { FreeStorage(); _completion.Dispose(); throw; }
        }

        ~ReceiverPipeStream() { Dispose(false); }

        internal bool IsIoPending { get { lock (_gate) return _inOperation; } }
        internal bool IsWritePending { get { lock (_gate) return _inOperation && _writing; } }

        /// <summary>Stops new IO and aborts pending IO; the owner drains completion before storage is released.</summary>
        internal void Cancel()
        {
            lock (_gate)
            {
                if (_stopped || _disposed) return;
                _stopped = true;
                if (!CancelIoEx(_handle, _overlapped))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 1168) _cancelError = error; // Already completed is an expected race.
                }
                _pipe.Dispose();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, RequestBytes, offset, count);
            if (count == 0) { lock (_gate) ThrowIfStopped(); return 0; }
            return Transfer(false, IntPtr.Add(_requestAddress, offset), count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, StatusBytes, offset, count);
            if (count == 0) { lock (_gate) ThrowIfStopped(); return; }
            while (count != 0)
            {
                var written = Transfer(true, IntPtr.Add(_statusAddress, offset), count);
                if (written == 0) throw new IOException("Receiver pipe write made no progress.");
                offset += written;
                count -= written;
            }
        }

        private int Transfer(bool write, IntPtr address, int count)
        {
            int error;
            lock (_gate)
            {
                ThrowIfStopped();
                if (_inOperation) throw new InvalidOperationException("Receiver IO must be serialized.");
                _inOperation = true;
                _writing = write;
                try
                {
                    // ReadFile/WriteFile reset this OVERLAPPED event before starting the operation.
                    Marshal.WriteIntPtr(_overlapped, 0, IntPtr.Zero);
                    Marshal.WriteIntPtr(_overlapped, IntPtr.Size, IntPtr.Zero);
                    var success = write ? WriteFile(_handle, address, count, IntPtr.Zero, _overlapped)
                        : ReadFile(_handle, address, count, IntPtr.Zero, _overlapped);
                    error = success ? 0 : Marshal.GetLastWin32Error();
                }
                catch { _inOperation = false; throw; }
            }
            try
            {
                if (error == 997) _completion.WaitOne();
                else if (error != 0) return Failure(write, error);
                lock (_gate)
                {
                    ThrowIfStopped();
                    int transferred;
                    if (!GetOverlappedResult(_handle, _overlapped, out transferred, false))
                        return Failure(write, Marshal.GetLastWin32Error());
                    return transferred;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _inOperation = false;
                    if (_disposeWaiting) Monitor.PulseAll(_gate);
                }
            }
        }

        private static int Failure(bool write, int error)
        {
            if (!write && (error == 109 || error == 232 || error == 233)) return 0;
            throw new IOException("Receiver pipe IO failed.", new Win32Exception(error));
        }

        private void ThrowIfStopped()
        {
#if NET
            ObjectDisposedException.ThrowIf(_disposed, this);
#else
            if (_disposed) throw new ObjectDisposedException("ReceiverPipeStream");
#endif
            if (_stopped) throw new IOException("Receiver pipe IO was canceled.",
                _cancelError == 0 ? null : new Win32Exception(_cancelError));
        }

        private static void ValidateBuffer(byte[] actual, byte[] owned, int offset, int count)
        {
#if NET
            if (!ReferenceEquals(actual, owned)) throw new ArgumentException("IO requires the receiver-owned buffer.", nameof(actual));
            if (offset < 0 || count < 0 || offset > owned.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
#else
            if (!ReferenceEquals(actual, owned)) throw new ArgumentException("IO requires the receiver-owned buffer.", "actual");
            if (offset < 0 || count < 0 || offset > owned.Length - count) throw new ArgumentOutOfRangeException("count");
#endif
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cancel();
            lock (_gate)
            {
                if (!_disposed)
                {
                    _disposeWaiting = true;
                    while (_inOperation) Monitor.Wait(_gate);
                    _disposed = true;
                    FreeStorage();
                    if (disposing) _completion.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void FreeStorage()
        {
            if (_overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_overlapped); _overlapped = IntPtr.Zero; }
            if (_requestPin.IsAllocated) _requestPin.Free();
            if (_statusPin.IsAllocated) _statusPin.Free();
        }

        public override bool CanRead { get { lock (_gate) return !_disposed; } }
        public override bool CanWrite { get { lock (_gate) return !_disposed; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        [StructLayout(LayoutKind.Sequential)]
        private struct OverlappedData
        {
            internal IntPtr Internal, InternalHigh;
            internal int Offset, OffsetHigh;
            internal IntPtr Event;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr file, IntPtr buffer, int count, IntPtr bytes, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr file, IntPtr buffer, int count, IntPtr bytes, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
    }
}
