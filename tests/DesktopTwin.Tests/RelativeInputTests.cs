using System.IO;
using DesktopTwin.Input;

namespace DesktopTwin.Tests;

public sealed class RelativeInputTests
{
    [Fact]
    public void RawRelativeMotionIsUnscaledAndAbsoluteDevicesRebaseOnDeviceOrDisplayChange()
    {
        var mouse = new RawMouseDeltas();
        Assert.Equal((17, -9), mouse.Read(1, 0, 17, -9, 1920, 1080));
        Assert.Equal((0, 0), mouse.Read(1, 1, 0, 0, 1920, 1080));
        Assert.Equal((1919, 1079), mouse.Read(1, 1, 65535, 65535, 1920, 1080));
        Assert.Equal((0, 0), mouse.Read(2, 1, 0, 0, 1920, 1080));
        Assert.Equal((0, 0), mouse.Read(2, 3, 65535, 65535, 3840, 1080));
        Assert.Equal((0, 0), mouse.Read(2, 3, -1, 0, 3840, 1080));
        Assert.Equal((0, 0), mouse.Read(2, 3, 32768, 0, 3840, 1080));
        mouse.Reset();
        Assert.Equal((0, 0), mouse.Read(2, 3, 65535, 0, 3840, 1080));
    }

    [Fact]
    public void MovementCoalescesWithoutReorderingButtonTransitionsOrLosingOverflow()
    {
        var queue = new InputPacketQueue();
        Assert.True(queue.TryAdd(new(InputOperation.Move, 10, 3)));
        Assert.True(queue.TryAdd(new(InputOperation.Move, 20, -3)));
        Assert.True(queue.TryAdd(new(InputOperation.Button, 1, 1)));
        Assert.True(queue.TryAdd(new(InputOperation.Move, -5, 9)));
        Assert.True(queue.TryTake(out var first));
        Assert.Equal((InputOperation.Move, 30, 0), (first.Operation, first.X, first.Y));
        Assert.True(queue.TryTake(out var second));
        Assert.Equal(InputOperation.Button, second.Operation);
        Assert.True(queue.TryTake(out var third));
        Assert.Equal(-5, third.X);
        Assert.False(queue.TryTake(out _));
        for (var index = 0; index < 128; index++) Assert.True(queue.TryAdd(new(InputOperation.Key, 0x11, index % 2)));
        Assert.False(queue.TryAdd(new(InputOperation.Key, 0x11, 0)));
        queue.Clear();
        Assert.False(queue.TryTake(out _));
    }

    [Fact]
    public void LeaseRevocationReleasesHeldInputsAndPreventsLateMovement()
    {
        var output = new List<InputPacket>();
        var lease = new InputLease(output.Add);
        lease.Apply(new(InputOperation.Move, 10, 0));
        Assert.Empty(output);
        lease.Apply(new(InputOperation.Heartbeat, 1));
        lease.Apply(new(InputOperation.Key, 0x11, 1));
        lease.Apply(new(InputOperation.Key, 0x11, 1)); // Autorepeat must not introduce a second held transition.
        lease.Apply(new(InputOperation.Button, 1, 1));
        lease.Apply(new(InputOperation.Move, 15, 0));
        lease.Apply(new(InputOperation.Heartbeat, 0));
        lease.Apply(new(InputOperation.Move, 20, 0));
        Assert.False(lease.Armed);
        Assert.Equal(0, lease.HeldCount);
        Assert.Equal(1, lease.Moves);
        Assert.Equal(new[] { InputOperation.Key, InputOperation.Button, InputOperation.Move, InputOperation.Key, InputOperation.Button },
            output.Select(packet => packet.Operation));
        Assert.All(output.TakeLast(2), packet => Assert.Equal(0, packet.Y));
        lease.Reset();
        Assert.Equal(5, output.Count);
    }

    [Fact]
    public void OpeningAMenuReleasesButtonsWithoutReleasingItsHeldAltKey()
    {
        var output = new List<InputPacket>();
        var lease = new InputLease(output.Add);
        lease.Apply(new(InputOperation.Heartbeat, 1));
        lease.Apply(new(InputOperation.Key, 0x38, 1));
        lease.Apply(new(InputOperation.Button, 1, 1));
        lease.Apply(new(InputOperation.ReleasePointer));
        Assert.Equal(1, lease.HeldCount);
        Assert.Equal(InputOperation.Button, output[^1].Operation);
        Assert.Equal(0, output[^1].Y);
        lease.Apply(new(InputOperation.Key, 0x38, 0));
        Assert.Equal(0, lease.HeldCount);
    }

    [Fact]
    public void FailedReleaseRetainsCleanupObligation()
    {
        var failRelease = true;
        var lease = new InputLease(packet =>
        {
            if (packet.Y == 0 && failRelease) throw new IOException("input desktop unavailable");
        });
        lease.Apply(new(InputOperation.Heartbeat, 1));
        lease.Apply(new(InputOperation.Key, 0x11, 1));
        Assert.Throws<IOException>(lease.Reset);
        Assert.Equal(1, lease.HeldCount);
        failRelease = false;
        lease.Reset();
        Assert.Equal(0, lease.HeldCount);
    }

    [Fact]
    public void DisconnectAttemptsEveryHeldReleaseEvenWhenOneBackendReleaseFails()
    {
        var failed = false;
        var releases = new List<InputPacket>();
        var lease = new InputLease(packet =>
        {
            if (packet.Y != 0) return;
            releases.Add(packet);
            if (failed && packet.Operation == InputOperation.Key) throw new IOException("key release failed");
        });
        lease.Apply(new(InputOperation.Heartbeat, 1));
        lease.Apply(new(InputOperation.Key, 0x11, 1));
        lease.Apply(new(InputOperation.Button, 1, 1));
        lease.Apply(new(InputOperation.Button, 2, 1));
        failed = true;
        Assert.Throws<IOException>(lease.Reset);
        Assert.Equal(3, releases.Count);
        Assert.Equal(1, lease.HeldCount);
        failed = false;
        lease.Reset();
        Assert.Equal(0, lease.HeldCount);
    }

    [Fact]
    public void ProtocolPreservesSignedDeltasAndRejectsTruncationAndUnknownOperations()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        RelativeInputProtocol.WritePacket(writer, new(InputOperation.Move, -90000, 60000));
        RelativeInputProtocol.WritePacket(writer, new(InputOperation.Key, 0x138, 0));
        bytes.Position = 0;
        using var reader = new BinaryReader(bytes);
        var move = RelativeInputProtocol.ReadPacket(reader);
        Assert.Equal((-90000, 60000), (move.X, move.Y));
        Assert.Equal(0x138, RelativeInputProtocol.ReadPacket(reader).X);
        Assert.Throws<EndOfStreamException>(() => RelativeInputProtocol.ReadPacket(reader));
        Assert.Throws<InvalidDataException>(() => new InputPacket((InputOperation)255));
        Assert.Throws<InvalidDataException>(() => new InputPacket(InputOperation.Button, 6, 1));
        Assert.Throws<InvalidDataException>(() => new InputPacket(InputOperation.Move, int.MinValue, 0));
        Assert.Throws<InvalidDataException>(() => new InputPacket(InputOperation.Key, 0x180, 1));
    }
}
