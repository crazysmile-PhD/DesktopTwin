using System.IO;
using System.Threading.Tasks.Sources;
using DesktopTwin.Input;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>Exercises completed and genuinely suspended IO without allocating a task in the test peer.</summary>
public sealed class RelativeInputAsyncTests
{
    [Fact]
    public async Task ABlockedWriteExpiresWithoutStartingARead()
    {
        using var blocked = new DelayedReplyStream(Timeout.Infinite, 0);
        using var transport = new RelativeInputTransport(blocked, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.ExchangeAsync(new(InputOperation.Heartbeat), 17).AsTask());
        Assert.False(blocked.WriteCompleted);
        Assert.Equal(0, blocked.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmReleaseExchangesDoNotAllocateWhenIoCompletesInlineOrSuspends(bool suspend)
    {
        using var stream = new ControlledReplyStream(suspend);
        using var transport = new RelativeInputTransport(stream, default);
        for (var i = 0; i < 1000; i++) ExchangeOnce(transport, stream);
        var before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (var i = 0; i < 10000; i++) checksum += ExchangeOnce(transport, stream).SessionId;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(170000, checksum);
        // Debug intentionally emits a class state machine; production Release emits the pooled struct.
#if !DEBUG
        Assert.Equal(0, allocated);
#endif
        Assert.Equal(11000, stream.Writes);
        Assert.Equal(suspend ? 22000 : 11000, stream.Reads);
        Assert.Equal(suspend ? stream.Writes : 0, stream.ConsumedWrites);
        Assert.Equal(suspend ? stream.Reads : 0, stream.ConsumedReads);
    }

    [Fact]
    public void SuspendedFramesKeepSnapshotsAndConsumeEveryIoResultExactlyOnce()
    {
        using var stream = new ControlledReplyStream(true);
        using var transport = new RelativeInputTransport(stream, default);
        var first = ExchangeOnce(transport, stream);
        var second = ExchangeOnce(transport, stream);
        var request = stream.Request.ToArray();
        Assert.Equal((1L, 2L), (first.InjectedMoves, second.InjectedMoves));
        Assert.Equal((2, 4), (stream.ConsumedWrites, stream.ConsumedReads));
        Assert.Equal((InputOperation.Move, -90000, 60000),
            ((InputOperation)request[0], RelativeInputProtocol.ReadInt32(request, 1), RelativeInputProtocol.ReadInt32(request, 5)));
    }

    [Fact]
    public async Task WriteAndReadShareOneDeadlineAndAPendingWriteObservesCancellation()
    {
        using var stop = new CancellationTokenSource();
        using var blocked = new DelayedReplyStream(Timeout.Infinite, 0);
        using (var transport = new RelativeInputTransport(blocked, stop.Token))
        {
            var exchange = transport.ExchangeAsync(new(InputOperation.Heartbeat), 17);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange.AsTask());
            Assert.False(blocked.WriteCompleted);
            Assert.Equal(0, blocked.Reads);
        }

        using var delayed = new DelayedReplyStream(650, 650);
        using var budgeted = new RelativeInputTransport(delayed, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            budgeted.ExchangeAsync(new(InputOperation.Heartbeat), 17).AsTask());
        Assert.True(delayed.WriteCompleted);
        Assert.Equal(1, delayed.Reads);
        Assert.False(delayed.ReadCompleted);
    }

    private static RelativeInputStatus ExchangeOnce(RelativeInputTransport transport, ControlledReplyStream stream)
    {
        var exchange = transport.ExchangeAsync(new(InputOperation.Move, -90000, 60000), 17);
        if (stream.Suspend)
        {
            if (exchange.IsCompleted || stream.ReadPending) throw new InvalidOperationException("Write was not awaited.");
            stream.CompleteWrite();
            if (exchange.IsCompleted || !stream.ReadPending) throw new InvalidOperationException("Read was not awaited.");
            stream.CompleteRead(5);
            if (exchange.IsCompleted || !stream.ReadPending) throw new InvalidOperationException("Partial frame was published.");
            stream.CompleteRead(31);
        }
        if (!exchange.IsCompletedSuccessfully) throw new InvalidOperationException("Frame did not complete inline.");
        return exchange.Result;
    }

    private sealed class ControlledReplyStream(bool suspend) : TestStream, IValueTaskSource, IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<bool> _write;
        private ManualResetValueTaskSourceCore<int> _read;
        private readonly byte[] _status = new byte[RelativeInputProtocol.StatusBytes];
        private Memory<byte> _destination;
        private int _offset;
        internal bool Suspend => suspend;
        internal bool ReadPending { get; private set; }
        internal ReadOnlyMemory<byte> Request { get; private set; }
        internal int Writes { get; private set; }
        internal int Reads { get; private set; }
        internal int ConsumedWrites { get; private set; }
        internal int ConsumedReads { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Request = buffer;
            Writes++;
            _offset = 0;
            RelativeInputProtocol.WriteInt32(_status, 0, 17);
            RelativeInputProtocol.WriteInt64(_status, 20, Writes);
            if (!suspend) return ValueTask.CompletedTask;
            _write.Reset();
            return new ValueTask(this, _write.Version);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (!suspend) { _status.CopyTo(buffer); return ValueTask.FromResult(_status.Length); }
            _destination = buffer;
            ReadPending = true;
            _read.Reset();
            return new ValueTask<int>(this, _read.Version);
        }

        internal void CompleteWrite() => _write.SetResult(true);
        internal void CompleteRead(int count)
        {
            _status.AsMemory(_offset, count).CopyTo(_destination);
            _offset += count;
            ReadPending = false;
            _read.SetResult(count);
        }

        void IValueTaskSource.GetResult(short token) { ConsumedWrites++; _write.GetResult(token); }
        int IValueTaskSource<int>.GetResult(short token) { ConsumedReads++; return _read.GetResult(token); }
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _write.GetStatus(token);
        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _read.GetStatus(token);
        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _write.OnCompleted(continuation, state, token, flags);
        void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _read.OnCompleted(continuation, state, token, flags);
    }

    private sealed class DelayedReplyStream(int writeDelay, int readDelay) : TestStream
    {
        internal bool WriteCompleted { get; private set; }
        internal bool ReadCompleted { get; private set; }
        internal int Reads { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(writeDelay, cancellationToken);
            WriteCompleted = true;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            await Task.Delay(readDelay, cancellationToken);
            ReadCompleted = true;
            return 0;
        }
    }

    private abstract class TestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
