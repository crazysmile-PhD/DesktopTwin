using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using DesktopTwin.Input;

namespace DesktopTwinInputReceiver
{
    internal static class ReceiverInput
    {
        internal static void Inject(InputPacket packet)
        {
            var input = new NativeInput();
            if (packet.Operation == InputOperation.Move)
                input.Data.Mouse = new MouseInput { X = packet.X, Y = packet.Y, Flags = 1 };
            else if (packet.Operation == InputOperation.Button)
            {
                uint[] down = { 0, 2, 8, 32, 128, 128 };
                uint[] up = { 0, 4, 16, 64, 256, 256 };
                input.Data.Mouse = new MouseInput { Flags = packet.Y == 1 ? down[packet.X] : up[packet.X],
                    Data = packet.X >= 4 ? (uint)(packet.X - 3) : 0 };
            }
            else if (packet.Operation == InputOperation.Key)
            {
                input.Type = 1;
                input.Data.Keyboard = new KeyboardInput { Scan = (ushort)(packet.X & 0x7F),
                    Flags = 8u | (packet.Y == 0 ? 2u : 0u) | ((packet.X & 0x100) != 0 ? 1u : 0u) };
            }
            else if (packet.Operation == InputOperation.Wheel)
                input.Data.Mouse = new MouseInput { Flags = packet.X == 0 ? 0x800u : 0x1000u, Data = unchecked((uint)packet.Y) };
            else throw new InvalidDataException("Not an input transition.");
            if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeInput))) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Session input insertion failed.");
        }

        internal static void WriteStatus(BinaryWriter writer, int session, InputLease lease)
        {
            var cursor = new CursorInfo { Size = (uint)Marshal.SizeOf(typeof(CursorInfo)) };
            bool available = GetCursorInfo(ref cursor);
            uint foreground;
            GetWindowThreadProcessId(GetForegroundWindow(), out foreground);
            writer.Write(session);
            writer.Write(foreground);
            writer.Write(available ? ((cursor.Flags & 1) != 0 ? 1 : 0) : -1);
            writer.Write(cursor.Point.X);
            writer.Write(cursor.Point.Y);
            writer.Write(lease.Moves);
            writer.Write(0); // Reserved status/error field; failed insertion closes the channel instead.
            writer.Write(lease.HeldCount);
            writer.Flush();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput { internal uint Type; internal InputData Data; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputData
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo { internal uint Size, Flags; internal IntPtr Cursor; internal Point Point; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    }
}
