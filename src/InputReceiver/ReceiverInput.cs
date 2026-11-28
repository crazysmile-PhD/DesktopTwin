using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using DesktopTwin.Input;

namespace DesktopTwinInputReceiver
{
    /// <summary>The serialized receiver owns one native input buffer for its entire lifetime.</summary>
    internal sealed class ReceiverInput
    {
        private readonly NativeInput[] _input = new NativeInput[1];
        private static readonly int NativeInputSize = Marshal.SizeOf<NativeInput>();
        private static readonly uint CursorInfoSize = (uint)Marshal.SizeOf<CursorInfo>();

        internal void Inject(InputPacket packet)
        {
            if (SendInput(1, Prepare(packet), NativeInputSize) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Session input insertion failed.");
        }

        /// <summary>Builds one transition in owned storage, clearing the union before every reuse.</summary>
        internal NativeInput[] Prepare(InputPacket packet)
        {
            var input = new NativeInput();
            if (packet.Operation == InputOperation.Move)
                input.Data.Mouse = new MouseInput { X = packet.X, Y = packet.Y, Flags = 1 };
            else if (packet.Operation == InputOperation.Button)
            {
                input.Data.Mouse = new MouseInput { Flags = ButtonFlags(packet.X, packet.Y == 1),
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
            _input[0] = input;
            return _input;
        }

        private static uint ButtonFlags(int button, bool down)
        {
            switch (button)
            {
                case 1: return down ? 2u : 4u;
                case 2: return down ? 8u : 16u;
                case 3: return down ? 32u : 64u;
                default: return down ? 128u : 256u;
            }
        }

        internal static void WriteStatus(byte[] bytes, int session, InputLease lease)
        {
            var cursor = new CursorInfo { Size = CursorInfoSize };
            bool available = GetCursorInfo(ref cursor);
            uint foreground;
            if (GetWindowThreadProcessId(GetForegroundWindow(), out foreground) == 0) foreground = 0;
            RelativeInputProtocol.WriteInt32(bytes, 0, session);
            RelativeInputProtocol.WriteInt32(bytes, 4, unchecked((int)foreground));
            RelativeInputProtocol.WriteInt32(bytes, 8, available ? ((cursor.Flags & 1) != 0 ? 1 : 0) : -1);
            RelativeInputProtocol.WriteInt32(bytes, 12, cursor.Point.X);
            RelativeInputProtocol.WriteInt32(bytes, 16, cursor.Point.Y);
            RelativeInputProtocol.WriteInt64(bytes, 20, lease.Moves);
            RelativeInputProtocol.WriteInt32(bytes, 28, 0); // Failed insertion closes the channel instead.
            RelativeInputProtocol.WriteInt32(bytes, 32, lease.HeldCount);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeInput { internal uint Type; internal InputData Data; }
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputData
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
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
