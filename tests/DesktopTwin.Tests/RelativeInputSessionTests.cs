using System.IO.Pipes;
using System.Reflection;
using DesktopTwin.Input;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>Isolated serial-loop failures use real pipes and no input injection or authentication bypass in production.</summary>
public sealed class RelativeInputSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExchangeFailureClosesThePeerBeforeSessionDisposal(bool missingReply)
    {
        using var session = new RelativeInputSession();
        var pipeField = Assert.IsAssignableFrom<FieldInfo>(typeof(RelativeInputSession).GetField("_pipe", BindingFlags.Instance | BindingFlags.NonPublic));
        var pipe = Assert.IsType<NamedPipeServerStream>(pipeField.GetValue(session));
        using var peer = new NamedPipeClientStream(".", session.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(pipe.WaitForConnectionAsync(timeout.Token), peer.ConnectAsync(timeout.Token));
        // Exercise loop teardown alone: the authenticated bootstrap is covered by real companion tests.
        var run = Assert.IsAssignableFrom<MethodInfo>(typeof(RelativeInputSession).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic));
        var loop = Assert.IsAssignableFrom<Task>(run.Invoke(session, [17u]));
        await peer.ReadExactlyAsync(new byte[RelativeInputProtocol.PacketBytes], timeout.Token);
        if (!missingReply)
        {
            var reply = new byte[RelativeInputProtocol.StatusBytes];
            RelativeInputProtocol.WriteInt32(reply, 0, 18); // Unexpected receiver session ends the exchange.
            await peer.WriteAsync(reply, timeout.Token);
        }
        await loop.WaitAsync(timeout.Token);
        Assert.NotNull(session.Failure);
        Assert.False(session.Ready);
        // No explicit session.Dispose yet: the failed loop must release its borrowed native handle itself.
        Assert.Equal(0, await peer.ReadAsync(new byte[1], timeout.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
