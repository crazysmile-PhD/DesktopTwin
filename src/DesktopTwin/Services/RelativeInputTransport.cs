using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Input;

namespace DesktopTwin.Services;

/// <summary>One serialized exchange owner; owns codec buffers, while its caller owns the stream.</summary>
internal sealed class RelativeInputTransport : IDisposable
{
    private readonly Stream _stream;
    private readonly InputExchangeDeadline _deadline;
    private readonly byte[] _requestBytes = new byte[RelativeInputProtocol.PacketBytes];
    private readonly byte[] _statusBytes = new byte[RelativeInputProtocol.StatusBytes];

    internal RelativeInputTransport(Stream stream, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _deadline = new InputExchangeDeadline(stop);
    }

    /// <summary>The serialized owner awaits each result once; a timeout permanently ends the transport.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<RelativeInputStatus> ExchangeAsync(InputPacket packet, uint sessionId)
    {
        _deadline.Begin();
        try
        {
            RelativeInputProtocol.WritePacket(_requestBytes, packet);
            await _stream.WriteAsync(_requestBytes, _deadline.Token).ConfigureAwait(false);
            await _stream.ReadExactlyAsync(_statusBytes, _deadline.Token).ConfigureAwait(false);
            var status = RelativeInputStatus.Read(_statusBytes);
            if (status.SessionId != sessionId) throw new IOException("Input receiver session changed.");
            return status;
        }
        finally { _deadline.End(); }
    }

    public void Dispose()
    {
        _deadline.Dispose();
    }
}
