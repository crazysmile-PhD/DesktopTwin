using System.IO;

namespace DesktopTwin.Input;

/// <summary>Evidence reported inside the destination session, rather than inferred from host cursor state.</summary>
internal sealed record RelativeInputStatus(int SessionId, uint ForegroundProcess, bool? CursorVisible,
    int CursorX, int CursorY, long InjectedMoves, int HeldCount)
{
    internal static RelativeInputStatus Read(BinaryReader reader)
    {
        var session = reader.ReadInt32();
        var foreground = reader.ReadUInt32();
        var visible = reader.ReadInt32();
        var x = reader.ReadInt32();
        var y = reader.ReadInt32();
        var moves = reader.ReadInt64();
        var error = reader.ReadInt32();
        var held = reader.ReadInt32();
        if (visible is < -1 or > 1 || moves < 0 || held < 0 || error != 0)
            throw new InvalidDataException("Invalid receiver status.");
        return new(session, foreground, visible < 0 ? null : visible != 0, x, y, moves, held);
    }
}
