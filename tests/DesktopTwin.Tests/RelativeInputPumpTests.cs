using System.IO;
using DesktopTwin.Input;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>Runs the production pump with a scripted wire peer and a virtual millisecond clock.</summary>
public sealed class RelativeInputPumpTests
{
    [Fact]
    public async Task QueuedRepliesReplaceStatusHeartbeatsAndKeepTransitionOrder()
    {
        await using var rig = new Rig { Until = 10000 };
        rig.Stream.OnReply = packet =>
        {
            if (rig.Stream.Count == 2)
            {
                rig.Enqueue(new(InputOperation.Key, 1, 1));
                rig.Enqueue(new(InputOperation.Button, 4, 1));
                rig.Enqueue(new(InputOperation.Key, 1, 0));
                rig.Enqueue(new(InputOperation.Button, 4, 0));
                rig.Request();
            }
            if (packet.Operation == InputOperation.Button && packet.Y == 0) { rig.Until = 0; rig.Signal.Set(); }
        };
        var run = rig.Run();
        Assert.False(run.IsCompleted);
        Assert.Equal(new[] { (InputOperation.Heartbeat, 0, 0), (InputOperation.Heartbeat, 1, 0),
            (InputOperation.Key, 1, 1), (InputOperation.Button, 4, 1), (InputOperation.Key, 1, 0),
            (InputOperation.Button, 4, 0), (InputOperation.Heartbeat, 0, 0) }, rig.Packets());
        Assert.Equal(7, rig.Published);
    }

    [Fact]
    public async Task QueuedInputAtTheHeartbeatDeadlineProvidesTheRequestedStatus()
    {
        await using var rig = new Rig { Until = 10000 };
        rig.Stream.SuspendGrant = true;
        _ = rig.Run();
        rig.BeforeTakeRequest = () => { rig.Now = 50; rig.BeforeTakeRequest = null; };
        rig.Enqueue(new(InputOperation.ReleasePointer));
        rig.Request();
        rig.Stream.CompleteReply();
        await rig.WaitPublishedAsync(3);
        Assert.Equal(new[] { InputOperation.Heartbeat, InputOperation.Heartbeat, InputOperation.ReleasePointer },
            rig.Packets().Select(p => p.Operation));
    }

    [Fact]
    public async Task NewInputKeepsTheHeartbeatDeadlineFresh()
    {
        await using var rig = new Rig { Until = 10000 };
        _ = rig.Run();
        for (var i = 0; i < 5; i++)
        {
            rig.Now += 49;
            rig.Enqueue(new(InputOperation.ReleasePointer));
            await rig.WaitPublishedAsync(3 + i);
        }
        Assert.Equal(2, rig.Packets().Count(p => p.Operation == InputOperation.Heartbeat));
        Assert.Equal(5, rig.Packets().Count(p => p.Operation == InputOperation.ReleasePointer));
    }

    [Fact]
    public async Task IdleNotificationsDoNotQueryAndPassiveRequestsDoNotAuthorize()
    {
        await using var rig = new Rig();
        var run = rig.Run();
        for (var i = 0; i < 1000; i++) rig.Signal.Set();
        rig.Request();
        await rig.WaitPublishedAsync(2);
        Assert.False(run.IsCompleted);
        Assert.Equal(new[] { (InputOperation.Heartbeat, 0, 0), (InputOperation.Heartbeat, 0, 0) }, rig.Packets());
    }

    [Fact]
    public async Task ExpiredAuthorizationDropsPendingTransitionsAndRevokesOnce()
    {
        await using var rig = new Rig { Until = 250 };
        rig.Enqueue(new(InputOperation.Key, 1, 1));
        rig.Enqueue(new(InputOperation.Button, 1, 1));
        rig.Stream.SuspendGrant = true;
        _ = rig.Run();
        rig.Now = 250;
        rig.Stream.CompleteReply();
        await rig.WaitPublishedAsync(3);
        Assert.Equal(new[] { (InputOperation.Heartbeat, 0, 0), (InputOperation.Heartbeat, 1, 0),
            (InputOperation.Heartbeat, 0, 0) }, rig.Packets());
        Assert.False(rig.Queue.TryTake(out _));
    }

    [Fact]
    public async Task RevocationDuringAGrantReplyPreventsPendingInput()
    {
        await using var rig = new Rig { Until = 250 };
        rig.Enqueue(new(InputOperation.Key, 1, 1));
        rig.Stream.SuspendGrant = true;
        _ = rig.Run();
        Assert.True(rig.Stream.HasPendingReply);
        rig.Until = 0;
        rig.Signal.Set();
        rig.Stream.CompleteReply();
        await rig.WaitPublishedAsync(3);
        Assert.Equal(new[] { (InputOperation.Heartbeat, 0, 0), (InputOperation.Heartbeat, 1, 0),
            (InputOperation.Heartbeat, 0, 0) }, rig.Packets());
    }

    [Fact]
    public async Task RevocationAndRenewalDuringAGrantReplyCannotResurrectClearedInput()
    {
        await using var rig = new Rig { Until = 250 };
        rig.Enqueue(new(InputOperation.Key, 1, 1));
        rig.Enqueue(new(InputOperation.Key, 1, 0));
        rig.Stream.SuspendGrant = true;
        _ = rig.Run();
        Assert.True(rig.Stream.HasPendingReply);
        rig.Until = 0;
        rig.Queue.Clear(); // SetForegroundLease(false) invalidates both queued transitions.
        rig.Signal.Set();
        rig.Until = 500;
        rig.Signal.Set();
        rig.Enqueue(new(InputOperation.ReleasePointer)); // New input after renewal remains valid.
        rig.Request();
        rig.Stream.SuspendGrant = false;
        rig.Stream.CompleteReply();
        await rig.WaitPublishedAsync(3);
        Assert.Equal(new[] { (InputOperation.Heartbeat, 0, 0), (InputOperation.Heartbeat, 1, 0),
            (InputOperation.ReleasePointer, 0, 0) }, rig.Packets());
        Assert.False(rig.Queue.TryTake(out _));
    }

    [Fact]
    public async Task RequestsArrivingDuringTheLastInputStillReceiveANewReply()
    {
        await using var rig = new Rig { Until = 10000 };
        _ = rig.Run();
        rig.Stream.OnReply = packet => { if (packet.Operation == InputOperation.ReleasePointer) rig.Request(); };
        rig.Enqueue(new(InputOperation.ReleasePointer));
        await rig.WaitPublishedAsync(4);
        Assert.Equal(new[] { InputOperation.Heartbeat, InputOperation.Heartbeat,
            InputOperation.ReleasePointer, InputOperation.Heartbeat }, rig.Packets().Select(p => p.Operation));
    }

    [Fact]
    public async Task CancellationDuringStateInspectionDoesNotNeedAnotherWake()
    {
        await using var rig = new Rig();
        rig.BeforeTakeRequest = () => rig.Stop.Cancel();
        var run = rig.Run();
        Assert.True(run.IsCompletedSuccessfully);
        Assert.Single(rig.Packets());
    }

    [Fact]
    public async Task AGrantBecomingValidAfterTheSnapshotStillPrecedesInput()
    {
        await using var rig = new Rig();
        rig.Enqueue(new(InputOperation.ReleasePointer));
        rig.BeforeTakeRequest = () => { rig.Until = 250; rig.BeforeTakeRequest = null; };
        _ = rig.Run();
        Assert.Equal(new[] { InputOperation.Heartbeat, InputOperation.Heartbeat, InputOperation.ReleasePointer },
            rig.Packets().Select(p => p.Operation));
    }

    [Fact]
    public async Task ExpiryBetweenQueuedPacketsDropsTheRemainder()
    {
        await using var rig = new Rig { Until = 100 };
        rig.Enqueue(new(InputOperation.Key, 1, 1));
        rig.Enqueue(new(InputOperation.Button, 1, 1));
        rig.Stream.OnReply = packet => { if (packet.Operation == InputOperation.Key) rig.Now = 100; };
        _ = rig.Run();
        Assert.Equal(new[] { InputOperation.Heartbeat, InputOperation.Heartbeat, InputOperation.Key, InputOperation.Heartbeat },
            rig.Packets().Select(p => p.Operation));
        Assert.False(rig.Queue.TryTake(out _));
    }

    [Fact]
    public async Task TransportFailureStopsThePumpAtTheFailedExchange()
    {
        await using var rig = new Rig { Until = 100 };
        rig.Stream.FailGrant = true;
        await Assert.ThrowsAsync<IOException>(() => rig.Run());
        Assert.Equal(2, rig.Stream.Count);
        Assert.Equal(1, rig.Published);
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal readonly InputPacketQueue Queue = new();
        internal readonly InputWakeSignal Signal = new();
        internal readonly CancellationTokenSource Stop = new();
        internal readonly PeerStream Stream = new();
        private readonly SemaphoreSlim _publishedSignal = new(0);
        private readonly RelativeInputTransport _transport;
        private Task? _run;
        private int _requested;
        internal int Published;
        internal long Now, Until;
        internal Action? BeforeTakeRequest;

        internal Rig() => _transport = new(Stream, Stop.Token);
        internal Task Run()
        {
            _run = new RelativeInputPump(_transport, Queue, Signal, () => Volatile.Read(ref Until), Take,
                _ => { Interlocked.Increment(ref Published); _publishedSignal.Release(); },
                () => Volatile.Read(ref Now)).RunAsync(17, Stop.Token);
            return _run;
        }
        private bool Take()
        {
            BeforeTakeRequest?.Invoke();
            return Interlocked.Exchange(ref _requested, 0) != 0;
        }
        internal void Enqueue(InputPacket packet)
        {
            Assert.True(Queue.TryAdd(packet, out var wake));
            if (wake) Signal.Set();
        }
        internal void Request() { Interlocked.Exchange(ref _requested, 1); Signal.Set(); }
        internal (InputOperation Operation, int X, int Y)[] Packets()
            => Stream.Snapshot().Select(p => (p.Operation, p.X, p.Y)).ToArray();
        internal async Task WaitPublishedAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref Published) < count) await _publishedSignal.WaitAsync(timeout.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel(); Signal.Set(); Stream.CancelReply();
            if (_run is not null) await Record.ExceptionAsync(() => _run.WaitAsync(TimeSpan.FromSeconds(5)));
            _transport.Dispose(); Signal.Dispose(); Stop.Dispose(); _publishedSignal.Dispose(); Stream.Dispose();
        }
    }

    private sealed class PeerStream : Stream
    {
        private readonly List<InputPacket> _packets = [];
        private readonly byte[] _status = new byte[36];
        private InputPacket _last;
        private Memory<byte> _destination;
        private TaskCompletionSource<int>? _pending;
        internal Action<InputPacket>? OnReply;
        internal bool SuspendGrant, FailGrant;
        internal bool HasPendingReply => _pending is not null;
        internal int Count { get { lock (_packets) return _packets.Count; } }
        internal InputPacket[] Snapshot() { lock (_packets) return _packets.ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = buffer.ToArray();
            _last = new((InputOperation)frame[0], RelativeInputProtocol.ReadInt32(frame, 1), RelativeInputProtocol.ReadInt32(frame, 5));
            lock (_packets) _packets.Add(_last);
            if (FailGrant && _last.Operation == InputOperation.Heartbeat && _last.X == 1) throw new IOException("synthetic grant failure");
            return ValueTask.CompletedTask;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _destination = buffer;
            OnReply?.Invoke(_last);
            if (SuspendGrant && _last.Operation == InputOperation.Heartbeat && _last.X == 1)
            {
                _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return new ValueTask<int>(_pending.Task);
            }
            Fill(); return ValueTask.FromResult(36);
        }
        private void Fill() { RelativeInputProtocol.WriteInt32(_status, 0, 17); _status.AsMemory().CopyTo(_destination); }
        internal void CompleteReply() { Fill(); _pending?.SetResult(36); _pending = null; }
        internal void CancelReply() { _pending?.TrySetCanceled(); _pending = null; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
