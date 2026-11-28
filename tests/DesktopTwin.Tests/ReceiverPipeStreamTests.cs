using System.IO;
using System.IO.Pipes;
using DesktopTwin.Input;
using DesktopTwinInputReceiver;

namespace DesktopTwin.Tests;

/// <summary>Real overlapped pipes and the shipped loop; fake injection records transitions without SendInput.</summary>
[Collection(WindowsDesktopTestGroup.Name)]
public sealed class ReceiverPipeStreamTests
{
    [Fact]
    public Task ReusedCompletionEventsCannotCompleteTheNextPendingRead() => WithPipeAsync(async (server, io) =>
    {
        for (var round = 0; round < 8; round++)
        {
            await server.WriteAsync(new byte[] { (byte)round });
            Assert.Equal(1, io.Read(io.RequestBytes, 0, 1));
            Assert.Equal((byte)round, io.RequestBytes[0]);
            var read = Task.Run(() => io.Read(io.RequestBytes, 0, 1));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!io.IsIoPending && !read.IsCompleted) await Task.Delay(1, timeout.Token);
            await Task.Delay(30, timeout.Token);
            Assert.False(read.IsCompleted);
            await server.WriteAsync(new byte[] { 123 }, timeout.Token);
            Assert.Equal(1, await read.WaitAsync(timeout.Token));
            Assert.Equal(123, io.RequestBytes[0]);
        }
    });

    [Theory]
    [InlineData("disconnect")]
    [InlineData("partial")]
    [InlineData("malformed")]
    [InlineData("timeout")]
    [InlineData("close")]
    public Task EveryTerminationReleasesHeldKeysAndButtonsOnce(string termination) => WithPipeAsync(async (server, io) =>
    {
        var injected = new List<InputPacket>();
        var lease = new InputLease(injected.Add);
        var loop = Task.Run(() => ReceiverLoop.Run(io, 17, lease));
        try
        {
            await HoldAsync(server);
            if (termination == "disconnect") server.Dispose();
            else if (termination == "partial") { await server.WriteAsync(new byte[] { 1 }); server.Dispose(); }
            else if (termination == "malformed") await server.WriteAsync(new byte[] { 255, 0, 0, 0, 0, 0, 0, 0, 0 });
            else if (termination == "close") Assert.Equal(0, (await ExchangeAsync(server, new(InputOperation.Close))).HeldCount);
            var failure = await Record.ExceptionAsync(() => loop.WaitAsync(TimeSpan.FromSeconds(5)));
            if (termination == "close") Assert.Null(failure);
            else if (termination == "malformed") Assert.IsType<InvalidDataException>(failure);
            else Assert.IsAssignableFrom<IOException>(failure);
            AssertReleased(injected, lease);
        }
        finally { io.Cancel(); await Record.ExceptionAsync(() => loop.WaitAsync(TimeSpan.FromSeconds(5))); }
    });

    [Fact]
    public Task LeaseRenewalSurvivesTheOldDeadlineAndRevocationReturnsToIdle() => WithPipeAsync(async (server, io) =>
    {
        var injected = new List<InputPacket>();
        var lease = new InputLease(injected.Add);
        var loop = Task.Run(() => ReceiverLoop.Run(io, 17, lease));
        try
        {
            await HoldAsync(server);
            await Task.Delay(900);
            Assert.Equal(2, (await ExchangeAsync(server, new(InputOperation.Heartbeat, 1))).HeldCount);
            await Task.Delay(900);
            Assert.False(loop.IsCompleted);
            Assert.Equal(0, (await ExchangeAsync(server, new(InputOperation.Heartbeat, 0))).HeldCount);
            await Task.Delay(1800);
            Assert.False(loop.IsCompleted);
            Assert.True(io.IsIoPending);
            await ExchangeAsync(server, new(InputOperation.Close));
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            AssertReleased(injected, lease);
        }
        finally { io.Cancel(); await Record.ExceptionAsync(() => loop.WaitAsync(TimeSpan.FromSeconds(5))); }
    });

    [Fact]
    public Task ABlockedReplyStillTimesOutAndReleasesHeldInput() => WithPipeAsync(async (server, io) =>
    {
        var injected = new List<InputPacket>();
        var lease = new InputLease(injected.Add);
        var loop = Task.Run(() => ReceiverLoop.Run(io, 17, lease));
        Task feed = Task.CompletedTask;
        try
        {
            await HoldAsync(server);
            feed = Task.Run(async () =>
            {
                var heartbeat = new byte[9];
                RelativeInputProtocol.WritePacket(heartbeat, new(InputOperation.Heartbeat, 1));
                for (var i = 0; i < 2048; i++) await server.WriteAsync(heartbeat);
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!io.IsWritePending) await Task.Delay(1, timeout.Token);
            await Task.Delay(50, timeout.Token);
            Assert.True(io.IsWritePending);
            await Assert.ThrowsAnyAsync<IOException>(() => loop.WaitAsync(timeout.Token));
            AssertReleased(injected, lease);
        }
        finally
        {
            io.Cancel(); server.Dispose();
            await Record.ExceptionAsync(() => loop.WaitAsync(TimeSpan.FromSeconds(5)));
            await Record.ExceptionAsync(() => feed.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    });

    [Fact]
    public async Task CancellationAndPeerCompletionRaceWithoutReusingPendingStorage()
    {
        for (var i = 0; i < 32; i++) await WithPipeAsync(async (server, io) =>
        {
            var read = Task.Run(() => RelativeInputProtocol.ReadPacket(io, io.RequestBytes));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!io.IsIoPending) await Task.Delay(1, timeout.Token);
            var cancel = Task.Run(io.Cancel, timeout.Token);
            var send = server.WriteAsync(new byte[9], timeout.Token).AsTask();
            var failure = await Record.ExceptionAsync(() => read.WaitAsync(timeout.Token));
            if (failure is not null) Assert.IsAssignableFrom<IOException>(failure);
            await cancel.WaitAsync(timeout.Token);
            await Record.ExceptionAsync(() => send.WaitAsync(timeout.Token));
            Assert.False(io.IsIoPending);
            Assert.Throws<IOException>(() => io.Read(io.RequestBytes, 0, 9));
            Assert.Throws<IOException>(() => io.Write(io.StatusBytes, 0, 36));
            io.Dispose(); io.Dispose();
            Assert.False(io.CanRead);
            Assert.False(io.CanWrite);
            Assert.Throws<ObjectDisposedException>(() => io.Read(io.RequestBytes, 0, 9));
        });
    }

    [Fact]
    public Task DisposalCancelsAndDrainsAPendingReadBeforeFreeingItsBuffers() => WithPipeAsync(async (_, io) =>
    {
        var read = Task.Run(() => io.Read(io.RequestBytes, 0, 9));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!io.IsIoPending) await Task.Delay(1, timeout.Token);
        await Task.Run(io.Dispose, timeout.Token).WaitAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<IOException>(() => read.WaitAsync(timeout.Token));
        Assert.False(io.IsIoPending);
    });

    [Fact]
    public Task WarmNativeReadWriteAndFragmentedFramesHaveNoPerExchangeAllocation() => WithPipeAsync(async (server, io) =>
    {
        var peer = Task.Run(async () =>
        {
            var request = new byte[9];
            RelativeInputProtocol.WritePacket(request, new(InputOperation.Move, -90000, 60000));
            var reply = new byte[36];
            for (var i = 0; i < 11000; i++)
            {
                await server.WriteAsync(request.AsMemory(0, 3));
                await server.WriteAsync(request.AsMemory(3));
                await server.ReadExactlyAsync(reply);
                if (RelativeInputProtocol.ReadInt32(reply, 0) != i) throw new InvalidDataException("Reply order changed.");
            }
        });
        try
        {
            for (var i = 0; i < 1000; i++) Frame(i);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 1000; i < 11000; i++) Frame(i);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            await peer.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { io.Cancel(); server.Dispose(); await Record.ExceptionAsync(() => peer.WaitAsync(TimeSpan.FromSeconds(5))); }

        void Frame(int sequence)
        {
            var packet = RelativeInputProtocol.ReadPacket(io, io.RequestBytes);
            if (packet.X != -90000 || packet.Y != 60000) throw new InvalidDataException("Fragmented packet changed.");
            RelativeInputProtocol.WriteInt32(io.StatusBytes, 0, sequence);
            io.Write(io.StatusBytes, 0, 36);
        }
    });

    [Fact]
    public Task OwnedBuffersAndSerializedIoAreRequired() => WithPipeAsync(async (_, io) =>
    {
        Assert.Throws<ArgumentException>(() => io.Read(new byte[9], 0, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => io.Write(io.StatusBytes, 0, 37));
        Assert.Equal(0, io.Read(io.RequestBytes, 0, 0));
        io.Write(io.StatusBytes, 0, 0);
        Assert.Throws<NotSupportedException>(() => io.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => io.SetLength(0));
        var read = Task.Run(() => io.Read(io.RequestBytes, 0, 9));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!io.IsIoPending) await Task.Delay(1, timeout.Token);
        Assert.Throws<InvalidOperationException>(() => io.Write(io.StatusBytes, 0, 36));
        io.Cancel();
        await Assert.ThrowsAnyAsync<IOException>(() => read.WaitAsync(timeout.Token));
    });

    private static async Task HoldAsync(NamedPipeServerStream server)
    {
        await ExchangeAsync(server, new(InputOperation.Heartbeat, 1));
        await ExchangeAsync(server, new(InputOperation.Key, 0x138, 1));
        Assert.Equal(2, (await ExchangeAsync(server, new(InputOperation.Button, 4, 1))).HeldCount);
    }

    private static async Task<RelativeInputStatus> ExchangeAsync(NamedPipeServerStream server, InputPacket packet)
    {
        var bytes = new byte[9];
        RelativeInputProtocol.WritePacket(bytes, packet);
        await server.WriteAsync(bytes);
        var reply = new byte[36];
        await server.ReadExactlyAsync(reply);
        return RelativeInputStatus.Read(reply);
    }

    private static void AssertReleased(List<InputPacket> injected, InputLease lease)
    {
        Assert.Equal(new[] { (InputOperation.Key, 0x138, 1), (InputOperation.Button, 4, 1),
            (InputOperation.Key, 0x138, 0), (InputOperation.Button, 4, 0) },
            injected.Select(p => (p.Operation, p.X, p.Y)).ToArray());
        Assert.False(lease.Armed);
        Assert.Equal(0, lease.HeldCount);
    }

    private static async Task WithPipeAsync(Func<NamedPipeServerStream, ReceiverPipeStream, Task> test)
    {
        var name = "DesktopTwin.ReceiverTest." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 128, 128);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        using var io = new ReceiverPipeStream(client);
        try { await test(server, io); }
        finally { io.Cancel(); }
    }
}
