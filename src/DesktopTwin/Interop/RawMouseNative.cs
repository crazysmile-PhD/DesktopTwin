using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopTwin.Interop;

/// <summary>Raw mouse registration is process-wide; preserve and conditionally restore its previous owner.</summary>
internal static class RawMouseNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Device { internal ushort Page, Usage; internal uint Flags; internal nint Window; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Header { internal uint Type, Size; internal nint Device, Parameter; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct Mouse
    {
        [FieldOffset(0)] internal ushort Flags;
        [FieldOffset(12)] internal int X;
        [FieldOffset(16)] internal int Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Packet { internal Header Header; internal Mouse Mouse; }

    internal static Device? RegisteredMouse()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<Device>();
        if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue || count > 64)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (count == 0) return null;
        var devices = new Device[count];
        if (GetRegisteredRawInputDevices(devices, ref count, size) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        foreach (var device in devices) if (device.Page == 1 && device.Usage == 2) return device;
        return null;
    }
    internal static void Register(Device device)
    {
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<Device>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    internal static bool Read(nint handle, out Packet packet)
    {
        var size = (uint)Marshal.SizeOf<Packet>();
        var result = GetRawInputData(handle, 0x10000003, out packet, ref size, (uint)Marshal.SizeOf<Header>());
        return result != uint.MaxValue && result >= size && packet.Header.Type == 0;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] Device[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint handle, uint command, out Packet packet, ref uint size, uint headerSize);
    [DllImport("user32.dll")]
    internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClipCursor(ref WindowNativeMethods.NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseClip(nint rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClipCursor(out WindowNativeMethods.NativeRect rect);
}
