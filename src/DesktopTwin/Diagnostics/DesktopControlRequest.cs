using System;

namespace DesktopTwin.Diagnostics;

/// <summary>显式启用的本地测试桥接所接受的有限命令，不接受路径、脚本或任意按键组合。</summary>
internal sealed record DesktopControlRequest(string Command, int X = 0, int Y = 0,
    string? Key = null, int Milliseconds = 100, int Width = 0, int Height = 0, bool Alt = false)
{
    internal void Validate()
    {
        if (Command is not ("state" or "connect" or "focus" or "launch_target" or "resize" or "mouse" or "click" or "key"
            or "probe_focus" or "release_mouse"))
            throw new ArgumentException("Unknown control command.");
        if (Milliseconds is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(Milliseconds));
        if (Command == "resize" && (Width is < 415 or > 7680 || Height is < 120 or > 4320))
            throw new ArgumentOutOfRangeException(nameof(Width));
        if (Command == "mouse" && (X is < -500 or > 500 || Y is < -500 or > 500))
            throw new ArgumentOutOfRangeException(nameof(X));
        if (Command == "key") _ = ScanCode(Key);
    }

    internal static ushort ScanCode(string? key) => key?.ToUpperInvariant() switch
    {
        "W" => 0x11, "A" => 0x1E, "S" => 0x1F, "D" => 0x20,
        "ESCAPE" => 0x01, "ENTER" => 0x1C, "TAB" => 0x0F, "ALT" => 0x38, "SPACE" => 0x39,
        _ => throw new ArgumentException("Only W/A/S/D, Escape, Enter, Tab, Alt and Space are accepted.", nameof(key))
    };
}
