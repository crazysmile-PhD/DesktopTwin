using System.IO;
using System.Runtime.InteropServices;
using DesktopTwin.Input;
using DesktopTwinInputReceiver;

namespace DesktopTwin.Tests;

/// <summary>Tests the shipped encoder source. Preparation and status inspection never call SendInput.</summary>
[Collection(WindowsDesktopTestGroup.Name)]
public sealed class ReceiverInputTests
{
    [Fact]
    public void PreparedPacketsKeepMouseKeyboardWheelFlagsAndNativeLayout()
    {
        var input = new ReceiverInput();
        var move = input.Prepare(new(InputOperation.Move, -90000, 60000))[0];
        Assert.Equal((0u, -90000, 60000, 1u), (move.Type, move.Data.Mouse.X, move.Data.Mouse.Y, move.Data.Mouse.Flags));
        uint[] down = [2, 8, 32, 128, 128];
        uint[] up = [4, 16, 64, 256, 256];
        for (var button = 1; button <= 5; button++)
        {
            var pressed = input.Prepare(new(InputOperation.Button, button, 1))[0];
            Assert.Equal(down[button - 1], pressed.Data.Mouse.Flags);
            Assert.Equal(button >= 4 ? (uint)(button - 3) : 0u, pressed.Data.Mouse.Data);
            Assert.Equal(up[button - 1], input.Prepare(new(InputOperation.Button, button, 0))[0].Data.Mouse.Flags);
        }
        var extended = input.Prepare(new(InputOperation.Key, 0x138, 1))[0];
        Assert.Equal((1u, (ushort)0x38, 9u), (extended.Type, extended.Data.Keyboard.Scan, extended.Data.Keyboard.Flags));
        Assert.Equal(11u, input.Prepare(new(InputOperation.Key, 0x138, 0))[0].Data.Keyboard.Flags);
        Assert.Equal(8u, input.Prepare(new(InputOperation.Key, 0x38, 1))[0].Data.Keyboard.Flags);
        Assert.Equal(0x800u, input.Prepare(new(InputOperation.Wheel, 0, -120))[0].Data.Mouse.Flags);
        var horizontal = input.Prepare(new(InputOperation.Wheel, 1, -120))[0];
        Assert.Equal((0x1000u, unchecked((uint)-120)), (horizontal.Data.Mouse.Flags, horizontal.Data.Mouse.Data));
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<ReceiverInput.NativeInput>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<ReceiverInput.NativeInput>(nameof(ReceiverInput.NativeInput.Data)).ToInt32());
        Assert.Throws<InvalidDataException>(() => input.Prepare(new(InputOperation.Heartbeat)));
    }

    [Fact]
    public void NativeBufferIsReusedAndItsUnionIsClearedForEachTransition()
    {
        var input = new ReceiverInput();
        var buffer = input.Prepare(new(InputOperation.Key, 0x138, 1));
        var saved = buffer[0];
        Assert.Same(buffer, input.Prepare(new(InputOperation.Move, 0, 0)));
        Assert.Equal((0u, 0, 0, 0u, 1u, 0u, UIntPtr.Zero),
            (buffer[0].Type, buffer[0].Data.Mouse.X, buffer[0].Data.Mouse.Y, buffer[0].Data.Mouse.Data,
                buffer[0].Data.Mouse.Flags, buffer[0].Data.Mouse.Time, buffer[0].Data.Mouse.Extra));
        Assert.Equal((1u, (ushort)0x38, 9u), (saved.Type, saved.Data.Keyboard.Scan, saved.Data.Keyboard.Flags));
        input.Prepare(new(InputOperation.Wheel, 0, -120));
        input.Prepare(new(InputOperation.Key, 1, 1));
        Assert.Equal((1u, (ushort)0, (ushort)1, 8u, 0u, UIntPtr.Zero),
            (buffer[0].Type, buffer[0].Data.Keyboard.Key, buffer[0].Data.Keyboard.Scan,
                buffer[0].Data.Keyboard.Flags, buffer[0].Data.Keyboard.Time, buffer[0].Data.Keyboard.Extra));
    }

    [Fact]
    public void WarmPreparationAndStatusEncodingDoNotAllocatePerPacket()
    {
        var input = new ReceiverInput();
        var move = new InputPacket(InputOperation.Move, 1, -1);
        var key = new InputPacket(InputOperation.Key, 0x138, 0);
        var lease = new InputLease(_ => { });
        var bytes = new byte[RelativeInputProtocol.StatusBytes];
        for (var i = 0; i < 1000; i++) { input.Prepare(move); ReceiverInput.WriteStatus(bytes, 17, lease); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
        {
            input.Prepare(i % 2 == 0 ? move : key);
            ReceiverInput.WriteStatus(bytes, 17, lease);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        var status = RelativeInputStatus.Read(bytes);
        Assert.Equal((17, 0L, 0), (status.SessionId, status.InjectedMoves, status.HeldCount));
    }
}
