using System.IO;

namespace DesktopTwin.Input;

/// <summary>Evidence reported inside the destination session, rather than inferred from host cursor state.</summary>
internal readonly record struct RelativeInputStatus(int SessionId, uint ForegroundProcess, bool? CursorVisible,
    int CursorX, int CursorY, long InjectedMoves, int HeldCount)
{
    /// <summary>Local arrival sequence, independent of the wire fields, distinguishes identical replies.</summary>
    public long Sequence { get; init; }

    internal static RelativeInputStatus Read(byte[] bytes)
    {
        if (bytes.Length != RelativeInputProtocol.StatusBytes) throw new InvalidDataException("Invalid receiver status length.");
        var session = RelativeInputProtocol.ReadInt32(bytes, 0);
        var foreground = unchecked((uint)RelativeInputProtocol.ReadInt32(bytes, 4));
        var visible = RelativeInputProtocol.ReadInt32(bytes, 8);
        var x = RelativeInputProtocol.ReadInt32(bytes, 12);
        var y = RelativeInputProtocol.ReadInt32(bytes, 16);
        var moves = RelativeInputProtocol.ReadInt64(bytes, 20);
        var error = RelativeInputProtocol.ReadInt32(bytes, 28);
        var held = RelativeInputProtocol.ReadInt32(bytes, 32);
        if (visible is < -1 or > 1 || moves < 0 || held < 0 || error != 0)
            throw new InvalidDataException("Invalid receiver status.");
        return new(session, foreground, visible < 0 ? null : visible != 0, x, y, moves, held);
    }
}
