using System;
using System.IO;

namespace DesktopTwin.Input
{
    // C# 5 compatible: the same codec is compiled into the session receiver.
    internal enum InputOperation : byte { Heartbeat, Move, Button, Key, Wheel, ReleasePointer, Reset, Close }

    internal struct InputPacket
    {
        internal readonly InputOperation Operation;
        internal readonly int X, Y;
        internal InputPacket(InputOperation operation, int x = 0, int y = 0)
        { Operation = operation; X = x; Y = y; Validate(); }
        internal void Validate()
        {
            if (Operation < InputOperation.Heartbeat || Operation > InputOperation.Close)
                throw new InvalidDataException("Unknown input operation.");
            if (Operation == InputOperation.Move && (Math.Abs((long)X) > 1000000 || Math.Abs((long)Y) > 1000000))
                throw new InvalidDataException("Movement exceeds protocol bounds.");
            if (Operation == InputOperation.Button && (X < 1 || X > 5 || Y < 0 || Y > 1))
                throw new InvalidDataException("Invalid button transition.");
            if (Operation == InputOperation.Key && (X < 1 || X > 0x17F || (X & 0xFF) > 0x7F || (X & 0x7F) == 0 || Y < 0 || Y > 1))
                throw new InvalidDataException("Invalid scan-code transition.");
            if (Operation == InputOperation.Heartbeat && (X < 0 || X > 1))
                throw new InvalidDataException("Invalid input lease.");
            if (Operation == InputOperation.Wheel && (X < 0 || X > 1 || Math.Abs((long)Y) > 120000))
                throw new InvalidDataException("Invalid wheel event.");
        }
    }

    internal static class RelativeInputProtocol
    {
        internal const int Version = 1;
        internal const int PacketBytes = 9;
        internal const int StatusBytes = 36;
        internal const int LeaseMilliseconds = 1500;
        internal static InputPacket ReadPacket(BinaryReader reader)
        { return new InputPacket((InputOperation)reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32()); }
        internal static void WritePacket(BinaryWriter writer, InputPacket packet)
        { packet.Validate(); writer.Write((byte)packet.Operation); writer.Write(packet.X); writer.Write(packet.Y); writer.Flush(); }
    }
}
