namespace DesktopTwin.Input;

/// <summary>Remembers complete gestures even when down/up both arrive between timer samples.</summary>
internal sealed class CaptureReleaseGesture
{
    private bool _alt;
    private bool _control;
    internal bool Released { get; private set; }

    internal void ObserveModifiers(bool alt, bool control)
    {
        if (alt && control) Released = true;
    }

    internal void ObserveMessage(int message, int key, bool physicalAlt)
    {
        if (message is 0x100 or 0x104 or 0x101 or 0x105)
        {
            var down = message is 0x100 or 0x104;
            if (key is 0x12 or 0xA4 or 0xA5) _alt = down;
            if (key is 0x11 or 0xA2 or 0xA3) _control = down;
            if (_alt && _control) Released = true;
        }
        if ((message is 0x201 or 0x203) && !_alt && !physicalAlt) Released = false;
    }

    internal void ClearModifiers() { _alt = false; _control = false; }
}
