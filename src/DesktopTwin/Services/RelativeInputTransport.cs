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
    private readonly BinaryWriter _writer;
    private readonly BinaryReader _reader;

    internal RelativeInputTransport(Stream stream, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _writer = new BinaryWriter(new MemoryStream(_requestBytes, writable: true));
        _reader = new BinaryReader(new MemoryStream(_statusBytes, writable: false));
    }

    /// <summary>A timeout ends the transport; its canceled deadline is never renewed for another attempt.</summary>
    internal async Task<RelativeInputStatus> ExchangeAsync(InputPacket packet, uint sessionId)
    {
        _deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            _writer.BaseStream.Position = 0;
            RelativeInputProtocol.WritePacket(_writer, packet);
            await _stream.WriteAsync(_requestBytes, _deadline.Token).ConfigureAwait(false);
            await _stream.ReadExactlyAsync(_statusBytes, _deadline.Token).ConfigureAwait(false);
            _reader.BaseStream.Position = 0;
            var status = RelativeInputStatus.Read(_reader);
            if (status.SessionId != sessionId) throw new IOException("Input receiver session changed.");
            return status;
        }
        finally { _deadline.CancelAfter(Timeout.InfiniteTimeSpan); }
    }

    public void Dispose()
    {
        _writer.Dispose();
        _reader.Dispose();
        _deadline.Dispose();
    }
}
