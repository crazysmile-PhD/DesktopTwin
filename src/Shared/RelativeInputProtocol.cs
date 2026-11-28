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
        private void Validate()
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
        /// <summary>Reads one complete frame into caller-owned storage and validates it at construction.</summary>
        internal static InputPacket ReadPacket(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < PacketBytes)
            {
                var read = stream.Read(buffer, offset, PacketBytes - offset);
                if (read == 0) throw new EndOfStreamException("Incomplete input packet.");
                offset += read;
            }
            return new InputPacket((InputOperation)buffer[0], ReadInt32(buffer, 1), ReadInt32(buffer, 5));
        }

        /// <summary>Encodes an already validated immutable packet; the caller owns the stream write.</summary>
        internal static void WritePacket(byte[] buffer, InputPacket packet)
        {
            buffer[0] = (byte)packet.Operation;
            WriteInt32(buffer, 1, packet.X);
            WriteInt32(buffer, 5, packet.Y);
        }

        internal static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24;
        }

        internal static long ReadInt64(byte[] buffer, int offset)
        {
            return (uint)ReadInt32(buffer, offset) | (long)ReadInt32(buffer, offset + 4) << 32;
        }

        internal static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        internal static void WriteInt64(byte[] buffer, int offset, long value)
        {
            WriteInt32(buffer, offset, (int)value);
            WriteInt32(buffer, offset + 4, (int)(value >> 32));
        }
    }
}
