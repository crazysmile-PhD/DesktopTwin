using System;
using System.Drawing;
using System.Windows.Forms;
using DesktopTwin.Input;
using DesktopTwin.Services;

namespace DesktopTwin.Interop;

/// <summary>Observes RDP gestures even while released; captured raw motion replaces legacy motion.</summary>
internal sealed class RelativeMouseCapture(Func<InputPacket, bool> send, Action<int, int, bool> observe) : NativeWindow, IDisposable
{
    private readonly RawMouseDeltas _deltas = new();
    private RawMouseNative.Device? _previousRegistration;
    private WindowNativeMethods.NativeRect _clip;
    private bool _ownsClip;
    internal bool Active { get; private set; }
    internal long RawMoves { get; private set; }

    internal void Observe(nint inputWindow)
    {
        if (Handle == inputWindow) return;
        Detach();
        AssignHandle(inputWindow);
    }
    internal void Start(nint inputWindow, WindowNativeMethods.NativeRect bounds)
    {
        Observe(inputWindow);
        if (Active) return;
        _previousRegistration = RawMouseNative.RegisteredMouse();
        Active = true;
        try
        {
            RawMouseNative.Register(new RawMouseNative.Device { Page = 1, Usage = 2, Window = inputWindow });
            _deltas.Reset();
            Constrain(bounds);
        }
        catch { Stop(); throw; }
    }
    internal void Constrain(WindowNativeMethods.NativeRect bounds)
    {
        var x = bounds.Left + (bounds.Right - bounds.Left) / 2;
        var y = bounds.Top + (bounds.Bottom - bounds.Top) / 2;
        if (_ownsClip && _clip.Left == x && _clip.Top == y) return;
        ReleaseConfinement();
        _clip = new WindowNativeMethods.NativeRect { Left = x, Top = y, Right = x + 1, Bottom = y + 1 };
        DiagnosticInput.MoveTo(x, y);
        if (!RawMouseNative.ClipCursor(ref _clip)) throw new System.ComponentModel.Win32Exception();
        _ownsClip = true;
    }
    private void ReleaseConfinement()
    {
        if (!_ownsClip) return;
        // A different foreground app may have acquired its own clip already. Never release that one.
        if (RawMouseNative.GetClipCursor(out var current) && current.Left == _clip.Left && current.Top == _clip.Top
            && current.Right == _clip.Right && current.Bottom == _clip.Bottom) RawMouseNative.ReleaseClip(0);
        _ownsClip = false;
    }
    internal void Stop()
    {
        ReleaseConfinement();
        if (!Active) return;
        try
        {
            if (RawMouseNative.RegisteredMouse()?.Window == Handle)
                RawMouseNative.Register(_previousRegistration ?? new RawMouseNative.Device { Page = 1, Usage = 2, Flags = 1 });
        }
        catch (System.ComponentModel.Win32Exception error) { AppLog.Warn("恢复原有鼠标注册失败。", error); }
        finally { Active = false; _deltas.Reset(); }
    }
    internal void Detach()
    {
        Stop();
        if (Handle != 0) ReleaseHandle();
    }
    protected override void WndProc(ref Message message)
    {
        var alt = RawMouseNative.GetAsyncKeyState(0x12) < 0;
        if (message.Msg is 0x100 or 0x101 or 0x104 or 0x105 or 0x201 or 0x203)
            observe(message.Msg, (int)message.WParam, alt);
        if (!Active) { base.WndProc(ref message); return; }
        if (alt) ReleaseConfinement();
        if (message.Msg == 0xFF && !alt)
        {
            if (RawMouseNative.Read(message.LParam, out var packet))
            {
                var area = (packet.Mouse.Flags & 2) != 0 ? SystemInformation.VirtualScreen : Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
                var delta = _deltas.Read(packet.Header.Device, packet.Mouse.Flags,
                    packet.Mouse.X, packet.Mouse.Y, area.Width, area.Height);
                if (delta != (0, 0)) { RawMoves++; send(new InputPacket(InputOperation.Move, delta.X, delta.Y)); }
            }
            message.Result = RawMouseNative.DefWindowProc(message.HWnd, (uint)message.Msg, message.WParam, message.LParam);
            return;
        }
        if (!alt && FilterMouse(message)) { message.Result = 0; return; }
        base.WndProc(ref message);
    }
    private bool FilterMouse(Message message)
    {
        if (message.Msg == 0x200) return true;
        if (message.Msg is 0x20A or 0x20E)
        {
            send(new InputPacket(InputOperation.Wheel, message.Msg == 0x20A ? 0 : 1, (short)((long)message.WParam >> 16)));
            return true;
        }
        var button = message.Msg switch
        {
            >= 0x201 and <= 0x203 => 1, >= 0x204 and <= 0x206 => 2, >= 0x207 and <= 0x209 => 3,
            >= 0x20B and <= 0x20D => 3 + (int)(((long)message.WParam >> 16) & 0xFFFF), _ => 0
        };
        if (button == 0) return false;
        var down = message.Msg is not (0x202 or 0x205 or 0x208 or 0x20C);
        send(new InputPacket(InputOperation.Button, button, down ? 1 : 0));
        return true;
    }
    public void Dispose() => Detach();
}
