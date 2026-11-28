using System.IO;
using System.IO.Pipes;
using DesktopTwin.Input;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>Protocol tests use an isolated peer and never authenticate, arm or inject into a desktop.</summary>
public sealed class RelativeInputTransportTests
{
    [Fact]
    public Task ReusedBuffersPreserveInputOrderAndReadFragmentedStatus() => WithPipeAsync(async (server, peer) =>
    {
        InputPacket[] expected = [new(InputOperation.Move, -90000, 60000), new(InputOperation.Button, 1, 1),
            new(InputOperation.Move, 7, -3), new(InputOperation.Button, 1, 0), new(InputOperation.Key, 0x138, 0)];
        using var transport = new RelativeInputTransport(server, default);
        var replies = Task.Run(async () =>
        {
            for (var i = 0; i < expected.Length; i++)
            {
                var request = new byte[RelativeInputProtocol.PacketBytes];
                await peer.ReadExactlyAsync(request);
                using var frame = new MemoryStream(request);
                var actual = RelativeInputProtocol.ReadPacket(frame, request);
                Assert.Equal((expected[i].Operation, expected[i].X, expected[i].Y), (actual.Operation, actual.X, actual.Y));
                var status = Status(session: 17, moves: i);
                for (var offset = 0; offset < status.Length; offset += 7)
                    await peer.WriteAsync(status.AsMemory(offset, Math.Min(7, status.Length - offset)));
            }
        });
        for (var i = 0; i < expected.Length; i++)
        {
            var status = await transport.ExchangeAsync(expected[i], 17);
            Assert.Equal(17, status.SessionId);
            Assert.Equal(i, status.InjectedMoves);
            Assert.True(status.CursorVisible);
            Assert.Equal((-12, 34), (status.CursorX, status.CursorY));
        }
        await replies;
    });

    [Theory]
    [InlineData(18, 0)]
    [InlineData(17, 1)]
    public Task WrongSessionOrInvalidStatusFailsTheExchange(int session, int error) => WithPipeAsync(async (server, peer) =>
    {
        using var transport = new RelativeInputTransport(server, default);
        var exchange = transport.ExchangeAsync(new(InputOperation.Heartbeat), 17);
        await peer.ReadExactlyAsync(new byte[RelativeInputProtocol.PacketBytes]);
        await peer.WriteAsync(Status(session, error: error));
        var failure = await Record.ExceptionAsync(() => exchange);
        if (error != 0) Assert.IsType<InvalidDataException>(failure);
        else Assert.IsType<IOException>(failure);
    });

    [Fact]
    public Task PartialStatusAndPeerDisconnectCannotBeReportedAsSuccess() => WithPipeAsync(async (server, peer) =>
    {
        using var transport = new RelativeInputTransport(server, default);
        var exchange = transport.ExchangeAsync(new(InputOperation.Heartbeat), 17);
        await peer.ReadExactlyAsync(new byte[RelativeInputProtocol.PacketBytes]);
        await peer.WriteAsync(Status(17).AsMemory(0, 5));
        peer.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => exchange);
    });

    [Fact]
    public Task MissingReplyTimesOutAndCannotRenewACanceledTransport() => WithPipeAsync(async (server, peer) =>
    {
        using var transport = new RelativeInputTransport(server, default);
        var exchange = transport.ExchangeAsync(new(InputOperation.Heartbeat), 17);
        await peer.ReadExactlyAsync(new byte[RelativeInputProtocol.PacketBytes]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.ExchangeAsync(new(InputOperation.Heartbeat), 17));
    });

    [Fact]
    public Task CallerCancellationStopsTheExchangeButCodecDisposalDoesNotOwnThePipe() => WithPipeAsync(async (server, peer) =>
    {
        using var stop = new CancellationTokenSource();
        var transport = new RelativeInputTransport(server, stop.Token);
        var exchange = transport.ExchangeAsync(new(InputOperation.Heartbeat), 17);
        await peer.ReadExactlyAsync(new byte[RelativeInputProtocol.PacketBytes]);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        transport.Dispose();
        var reply = new byte[1];
        await Task.WhenAll(server.WriteAsync(new byte[] { 42 }).AsTask(), peer.ReadExactlyAsync(reply).AsTask());
        Assert.Equal(42, reply[0]);
    });

    private static byte[] Status(int session, long moves = 0, int error = 0)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(session);
        writer.Write(100u);
        writer.Write(1);
        writer.Write(-12);
        writer.Write(34);
        writer.Write(moves);
        writer.Write(error);
        writer.Write(0);
        return memory.ToArray();
    }

    private static async Task WithPipeAsync(Func<NamedPipeServerStream, NamedPipeClientStream, Task> test)
    {
        var name = "DesktopTwin.transport-test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var peer = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), peer.ConnectAsync(timeout.Token));
        await test(server, peer).WaitAsync(timeout.Token);
    }
}
