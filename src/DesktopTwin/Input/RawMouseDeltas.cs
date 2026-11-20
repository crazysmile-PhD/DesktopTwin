using System;

namespace DesktopTwin.Input;

/// <summary>Relative devices bypass OS pointer acceleration; absolute devices establish a fresh baseline.</summary>
internal sealed class RawMouseDeltas
{
    private (nint Device, int Width, int Height, bool Virtual, int X, int Y)? _last;
    internal (int X, int Y) Read(nint device, ushort flags, int x, int y, int width, int height)
    {
        if ((flags & 1) == 0) { _last = null; return (x, y); }
        if (width < 1 || height < 1 || x is < 0 or > 65535 || y is < 0 or > 65535)
        { _last = null; return (0, 0); }
        var pointX = (int)((long)x * (width - 1) / 65535);
        var pointY = (int)((long)y * (height - 1) / 65535);
        var virtualDesktop = (flags & 2) != 0;
        var delta = _last is { } last && last.Device == device && last.Width == width
            && last.Height == height && last.Virtual == virtualDesktop
            ? (pointX - last.X, pointY - last.Y) : (0, 0);
        _last = (device, width, height, virtualDesktop, pointX, pointY);
        return delta;
    }
    internal void Reset() => _last = null;
}
