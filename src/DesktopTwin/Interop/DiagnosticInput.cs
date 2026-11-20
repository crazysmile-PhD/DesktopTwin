using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopTwin.Interop;

/// <summary>本地测试桥接的有界输入；调用者必须先核对前台、登录状态及 RDP 区域。</summary>
internal static class DiagnosticInput
{
    internal static void MoveTo(int x, int y)
    {
        if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal static (int X, int Y) Cursor()
    {
        if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (point.X, point.Y);
    }

    internal static object CaptureState()
    {
        var point = Cursor();
        var clipAvailable = GetClipCursor(out var clip);
        var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        var infoAvailable = GetCursorInfo(ref info);
        return new
        {
            x = point.X, y = point.Y,
            visible = infoAvailable ? (bool?)((info.Flags & 1) != 0) : null,
            captureWindow = GetCapture().ToInt64(),
            clip = clipAvailable ? new { left = clip.Left, top = clip.Top, right = clip.Right, bottom = clip.Bottom } : null
        };
    }

    internal static void Mouse(int dx, int dy, bool click)
    {
        var events = click
            ? new[] { MouseEvent(0, 0, 0x0002), MouseEvent(0, 0, 0x0004) }
            : new[] { MouseEvent(dx, dy, 0x0001) };
        Send(events);
    }

    internal static void Key(ushort scanCode, bool up)
    {
        var input = new InputEvent { Type = 1 };
        input.Data.Keyboard = new KeyboardInput { Scan = scanCode, Flags = 0x0008u | (up ? 0x0002u : 0) };
        Send([input]);
    }

    internal static void Button(bool up) => Send([MouseEvent(0, 0, up ? 0x0004u : 0x0002u)]);

    private static InputEvent MouseEvent(int dx, int dy, uint flags)
    {
        var input = new InputEvent();
        input.Data.Mouse = new MouseInput { X = dx, Y = dy, Flags = flags };
        return input;
    }

    private static void Send(InputEvent[] events)
    {
        var sent = SendInput((uint)events.Length, events, Marshal.SizeOf<InputEvent>());
        if (sent != events.Length) throw new Win32Exception(Marshal.GetLastPInvokeError(), "The test input was not fully inserted.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent { internal uint Type; internal InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey, Scan;
        internal uint Flags, Time;
        internal nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        internal uint Size, Flags;
        internal nint Cursor;
        internal NativePoint Position;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out WindowNativeMethods.NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")]
    private static extern nint GetCapture();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, InputEvent[] inputs, int size);
}
