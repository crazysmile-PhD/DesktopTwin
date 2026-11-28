using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Input;

namespace DesktopTwin.Services;

/// <summary>One serialized exchange owner; owns codec buffers, while its caller owns the stream.</summary>
internal sealed class RelativeInputTransport : IDisposable
{
    private readonly Stream _stream;
    private readonly CancellationTokenSource _deadline;
    private readonly byte[] _requestBytes = new byte[RelativeInputProtocol.PacketBytes];
    private readonly byte[] _statusBytes = new byte[RelativeInputProtocol.StatusBytes];

    internal RelativeInputTransport(Stream stream, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
    }

    /// <summary>A timeout ends the transport; its canceled deadline is never renewed for another attempt.</summary>
    internal async Task<RelativeInputStatus> ExchangeAsync(InputPacket packet, uint sessionId)
    {
        _deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            RelativeInputProtocol.WritePacket(_requestBytes, packet);
            await _stream.WriteAsync(_requestBytes, _deadline.Token).ConfigureAwait(false);
            await _stream.ReadExactlyAsync(_statusBytes, _deadline.Token).ConfigureAwait(false);
            var status = RelativeInputStatus.Read(_statusBytes);
            if (status.SessionId != sessionId) throw new IOException("Input receiver session changed.");
            return status;
        }
        finally { _deadline.CancelAfter(Timeout.InfiniteTimeSpan); }
    }

    public void Dispose()
    {
        _deadline.Dispose();
    }
}
