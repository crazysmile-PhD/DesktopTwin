using System.IO;
using DesktopTwin.Input;

namespace DesktopTwin.Tests;

public sealed class InputHotPathTests
{
    [Fact]
    public void OnlyAnEmptyQueueTransitionRequiresAWake()
    {
        var queue = new InputPacketQueue();
        Assert.True(queue.TryAdd(new(InputOperation.Move, 1, 0), out var wake));
        Assert.True(wake);
        Assert.True(queue.TryAdd(new(InputOperation.Move, 2, 0), out wake));
        Assert.False(wake);
        Assert.True(queue.TryAdd(new(InputOperation.Key, 1, 1), out wake));
        Assert.False(wake);
        Assert.True(queue.TryTake(out var move));
        Assert.Equal(3, move.X);
        Assert.True(queue.TryTake(out _));
        Assert.True(queue.TryAdd(new(InputOperation.Key, 1, 0), out wake));
        Assert.True(wake);
        queue.Clear();
        Assert.True(queue.TryAdd(new(InputOperation.ReleasePointer), out wake));
        Assert.True(wake);
    }

    [Fact]
    public void RingWraparoundPreservesTransitionsAndCoalescesOnlyItsLastMovement()
    {
        var queue = new InputPacketQueue();
        for (var i = 0; i < 100; i++) Assert.True(queue.TryAdd(new(InputOperation.Key, 1 + i, i % 2)));
        for (var i = 0; i < 90; i++) { Assert.True(queue.TryTake(out var packet)); Assert.Equal(1 + i, packet.X); }
        for (var i = 0; i < 117; i++) Assert.True(queue.TryAdd(new(InputOperation.Button, 1 + i % 5, i % 2)));
        Assert.True(queue.TryAdd(new(InputOperation.Move, 10, -10)));
        Assert.True(queue.TryAdd(new(InputOperation.Move, 20, 10))); // Full ring can still merge its final move.
        Assert.False(queue.TryAdd(new(InputOperation.Key, 1, 0)));
        for (var i = 90; i < 100; i++) { Assert.True(queue.TryTake(out var packet)); Assert.Equal(1 + i, packet.X); }
        for (var i = 0; i < 117; i++)
        {
            Assert.True(queue.TryTake(out var packet));
            Assert.Equal((InputOperation.Button, 1 + i % 5, i % 2), (packet.Operation, packet.X, packet.Y));
        }
        Assert.True(queue.TryTake(out var move));
        Assert.Equal((InputOperation.Move, 30, 0), (move.Operation, move.X, move.Y));
        Assert.False(queue.TryTake(out _));
        Assert.True(queue.TryAdd(new(InputOperation.Move, 1000000, -1000000)));
        Assert.False(queue.TryAdd(new(InputOperation.Move, 1, -1)));
        Assert.True(queue.TryTake(out move));
        Assert.Equal((1000000, -1000000), (move.X, move.Y));
        Assert.True(queue.TryAdd(new(InputOperation.Button, 1, 1)));
        queue.Clear();
        Assert.False(queue.TryTake(out _));
        Assert.True(queue.TryAdd(new(InputOperation.Key, 1, 0)));
        Assert.True(queue.TryTake(out var afterClear));
        Assert.Equal(InputOperation.Key, afterClear.Operation);
    }

    [Fact]
    public void WarmQueueAndStatusDecodeHaveNoPerPacketManagedAllocation()
    {
        var queue = new InputPacketQueue();
        var packet = new InputPacket(InputOperation.Move, 1, -1);
        var bytes = new byte[RelativeInputProtocol.StatusBytes];
        var frame = new byte[RelativeInputProtocol.PacketBytes];
        using var stream = new MemoryStream(frame);
        long checksum = 0;
        for (var i = 0; i < 1000; i++)
        {
            queue.TryAdd(packet); queue.TryTake(out _);
            checksum += RelativeInputStatus.Read(bytes).SessionId;
            RelativeInputProtocol.WritePacket(frame, packet);
            stream.Position = 0;
            RelativeInputProtocol.ReadPacket(stream, frame);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
        {
            if (!queue.TryAdd(packet) || !queue.TryTake(out var taken)) throw new InvalidOperationException("Queue failed.");
            RelativeInputProtocol.WritePacket(frame, packet);
            stream.Position = 0;
            var decoded = RelativeInputProtocol.ReadPacket(stream, frame);
            checksum += taken.X + RelativeInputStatus.Read(bytes).SessionId + decoded.X + decoded.Y;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10000, checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DecodedStatusRemainsAnIndependentSnapshotWhenTheBufferIsReused()
    {
        var bytes = new byte[RelativeInputProtocol.StatusBytes];
        RelativeInputProtocol.WriteInt32(bytes, 0, 17);
        RelativeInputProtocol.WriteInt32(bytes, 4, unchecked((int)uint.MaxValue));
        RelativeInputProtocol.WriteInt32(bytes, 8, -1);
        RelativeInputProtocol.WriteInt32(bytes, 12, -12345);
        RelativeInputProtocol.WriteInt64(bytes, 20, long.MaxValue);
        var first = RelativeInputStatus.Read(bytes);
        RelativeInputProtocol.WriteInt32(bytes, 0, 18);
        RelativeInputProtocol.WriteInt32(bytes, 8, 1);
        RelativeInputProtocol.WriteInt64(bytes, 20, 0);
        var second = RelativeInputStatus.Read(bytes);
        Assert.Equal((17, uint.MaxValue, (bool?)null, -12345, long.MaxValue),
            (first.SessionId, first.ForegroundProcess, first.CursorVisible, first.CursorX, first.InjectedMoves));
        Assert.Equal((18, (bool?)true, 0L), (second.SessionId, second.CursorVisible, second.InjectedMoves));
        RelativeInputProtocol.WriteInt32(bytes, 8, 2);
        Assert.Throws<InvalidDataException>(() => RelativeInputStatus.Read(bytes));
        Assert.Throws<InvalidDataException>(() => RelativeInputStatus.Read(new byte[35]));
    }

    [Fact]
    public void WireBoundaryRejectsMalformedPacketsAndInvalidStatusCounters()
    {
        (InputOperation Operation, int X, int Y)[] invalid =
        [
            ((InputOperation)255, 0, 0), (InputOperation.Move, int.MinValue, 0),
            (InputOperation.Button, 6, 1), (InputOperation.Button, 1, 2),
            (InputOperation.Key, 0, 1), (InputOperation.Key, 0x180, 1),
            (InputOperation.Heartbeat, 2, 0), (InputOperation.Wheel, 2, 0), (InputOperation.Wheel, 0, 120001)
        ];
        foreach (var value in invalid)
        {
            var frame = new byte[RelativeInputProtocol.PacketBytes];
            frame[0] = (byte)value.Operation;
            RelativeInputProtocol.WriteInt32(frame, 1, value.X);
            RelativeInputProtocol.WriteInt32(frame, 5, value.Y);
            using var stream = new MemoryStream(frame);
            Assert.Throws<InvalidDataException>(() => RelativeInputProtocol.ReadPacket(stream, frame));
        }
        var status = new byte[RelativeInputProtocol.StatusBytes];
        RelativeInputProtocol.WriteInt64(status, 20, -1);
        Assert.Throws<InvalidDataException>(() => RelativeInputStatus.Read(status));
        RelativeInputProtocol.WriteInt64(status, 20, 0);
        RelativeInputProtocol.WriteInt32(status, 32, -1);
        Assert.Throws<InvalidDataException>(() => RelativeInputStatus.Read(status));
    }

    [Fact]
    public void FixedPacketCodecPreservesWireBytesAndHandlesFragmentedOrTruncatedFrames()
    {
        var bytes = new byte[RelativeInputProtocol.PacketBytes];
        RelativeInputProtocol.WritePacket(bytes, new(InputOperation.Move, -90000, 60000));
        Assert.Equal(new byte[] { 1, 0x70, 0xA0, 0xFE, 0xFF, 0x60, 0xEA, 0, 0 }, bytes);
        using var complete = new FragmentedStream(bytes, 9);
        Assert.Equal(-90000, RelativeInputProtocol.ReadPacket(complete, new byte[9]).X);
        Assert.Equal(1, complete.Reads);
        using var fragmented = new FragmentedStream(bytes);
        var packet = RelativeInputProtocol.ReadPacket(fragmented, new byte[9]);
        Assert.Equal((-90000, 60000), (packet.X, packet.Y));
        using var truncated = new FragmentedStream(bytes[..8]);
        Assert.Throws<EndOfStreamException>(() => RelativeInputProtocol.ReadPacket(truncated, new byte[9]));
        bytes[0] = 255;
        using var invalid = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => RelativeInputProtocol.ReadPacket(invalid, new byte[9]));
        var signed = new byte[8];
        RelativeInputProtocol.WriteInt64(signed, 0, long.MinValue);
        Assert.Equal(long.MinValue, RelativeInputProtocol.ReadInt64(signed, 0));
    }

    private sealed class FragmentedStream(byte[] bytes, int chunkSize = 1) : MemoryStream(bytes)
    {
        internal int Reads { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, Math.Min(count, chunkSize));
        }
    }
}
