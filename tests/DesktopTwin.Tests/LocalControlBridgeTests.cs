using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DesktopTwin.Diagnostics;

namespace DesktopTwin.Tests;

public sealed class LocalControlBridgeTests
{
    [Fact]
    public void BridgeIsOptInAndRequiresAnUnambiguousLocalPipeName()
    {
        Assert.Null(LocalControlBridge.ReadPipeArgument([]));
        var name = "DesktopTwin.Control." + Guid.NewGuid().ToString("N");
        Assert.Equal(name, LocalControlBridge.ReadPipeArgument(["--control-pipe", name]));
        Assert.Throws<ArgumentException>(() => LocalControlBridge.ReadPipeArgument(["--control-pipe"]));
        Assert.Throws<ArgumentException>(() => LocalControlBridge.ReadPipeArgument(["--control-pipe", "\\\\other-host\\pipe\\control"]));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("Windows")]
    [InlineData("Control+Alt+Delete")]
    [InlineData("C:\\program.exe")]
    public void InputAllowlistRejectsShellsPathsAndSystemChords(string value)
    {
        Assert.Throws<ArgumentException>(() => new DesktopControlRequest(value).Validate());
        Assert.Throws<ArgumentException>(() => new DesktopControlRequest("key", Key: value).Validate());
    }

    [Fact]
    public void MovementDurationAndResizeRequestsAreBounded()
    {
        new DesktopControlRequest("mouse", X: 100, Y: -100).Validate();
        new DesktopControlRequest("key", Key: "w", Milliseconds: 250).Validate();
        new DesktopControlRequest("resize", Width: 960, Height: 700).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopControlRequest("mouse", X: 501).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopControlRequest("key", Key: "W", Milliseconds: 1001).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopControlRequest("resize", Width: 100, Height: 700).Validate());
    }

    [Fact]
    public async Task MalformedClientDoesNotStopTheNextValidRequest()
    {
        var name = "DesktopTwin.Control." + Guid.NewGuid().ToString("N");
        var calls = 0;
        using var bridge = new LocalControlBridge(name, (request, _) =>
        {
            calls++;
            return Task.FromResult<object>(new { command = request.Command });
        });
        using var rejected = await SendAsync(name, "{\"command\":\"exec\",\"key\":\"powershell\"}");
        Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, calls);
        using var accepted = await SendAsync(name, "{\"command\":\"state\"}");
        Assert.True(accepted.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("state", accepted.RootElement.GetProperty("result").GetProperty("command").GetString());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task InvalidPacketLengthsAreRejectedBeforeAllocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LocalControlBridge.ReadPacketAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task SlowReaderReceivesTheWholeReplyBeforeTheServerDisconnects()
    {
        var name = "DesktopTwin.Control." + Guid.NewGuid().ToString("N");
        using var bridge = new LocalControlBridge(name, (_, _) => Task.FromResult<object>(new { text = new string('x', 2000) }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await LocalControlBridge.WritePacketAsync(client, Encoding.UTF8.GetBytes("{\"command\":\"state\"}"), timeout.Token);
        await Task.Delay(150, timeout.Token);
        var bytes = await LocalControlBridge.ReadPacketAsync(client, timeout.Token);
        using var response = JsonDocument.Parse(bytes);
        Assert.Equal(2000, response.RootElement.GetProperty("result").GetProperty("text").GetString()?.Length);
        await client.WriteAsync(new byte[] { 1 }, timeout.Token);
    }

    [Fact]
    public async Task ClientDisconnectBeforeReplyDoesNotStopTheServer()
    {
        var name = "DesktopTwin.Control." + Guid.NewGuid().ToString("N");
        using var bridge = new LocalControlBridge(name, (request, _) => Task.FromResult<object>(new { command = request.Command }));
        for (var index = 0; index < 10; index++)
        {
            using (var abandoned = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await abandoned.ConnectAsync(2000);
                // End the connection halfway through its length prefix.
                await abandoned.WriteAsync(new byte[] { 10, 0 });
            }
            using var response = await SendAsync(name, "{\"command\":\"state\"}");
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    private static async Task<JsonDocument> SendAsync(string name, string json)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await LocalControlBridge.WritePacketAsync(client, Encoding.UTF8.GetBytes(json), timeout.Token);
        var bytes = await LocalControlBridge.ReadPacketAsync(client, timeout.Token);
        await client.WriteAsync(new byte[] { 1 }, timeout.Token);
        return JsonDocument.Parse(bytes);
    }
}
