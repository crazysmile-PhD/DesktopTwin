using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using DesktopTwin.Services;
using DesktopTwinInputReceiver;

namespace DesktopTwin.Tests;

/// <summary>Real overlapped host IO; all payloads are test bytes and no input is injected.</summary>
public sealed class InputPipeStreamTests
{
    [Fact]
    public Task PendingReadResumesOnItsCompletionWorkerAndCanDisposeInline() => WithPipeAsync(async (server, client) =>
    {
        var request = new byte[9]; var status = new byte[36];
        using var io = new InputPipeStream(server, request, status, default);
        var read = io.ReadAsync(status);
        Assert.False(read.IsCompleted);
        var completed = new TaskCompletionSource<(int Bytes, bool Inline)>(TaskCreationOptions.RunContinuationsAsynchronously);
        read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
        {
            try
            {
                var count = read.Result;
                var inline = new StackTrace().GetFrames().Any(frame => frame.GetMethod()?.DeclaringType == typeof(InputPipeStream));
                io.Dispose();
                completed.SetResult((count, inline));
            }
            catch (Exception error) { completed.SetException(error); }
        });
        await client.WriteAsync(new byte[36]);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(36, result.Bytes);
        Assert.True(result.Inline);
        Assert.True(server.CanRead); // The adapter borrows the caller's pipe.
    });

    [Fact]
    public Task FragmentedStatusKeepsOffsetsAndEofIsNotReportedAsAFullFrame() => WithPipeAsync(async (server, client) =>
    {
        var request = new byte[9]; var status = new byte[36];
        using var io = new InputPipeStream(server, request, status, default);
        var read = io.ReadExactlyAsync(status).AsTask();
        await client.WriteAsync(Enumerable.Range(0, 3).Select(i => (byte)i).ToArray());
        await client.WriteAsync(Enumerable.Range(3, 33).Select(i => (byte)i).ToArray());
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 36).Select(i => (byte)i), status);
        var truncated = io.ReadExactlyAsync(status).AsTask();
        await client.WriteAsync(new byte[3]);
        client.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => truncated.WaitAsync(TimeSpan.FromSeconds(5)));
    });

    [Fact]
    public Task PendingResultsMustBeReadyAndConsumedBeforeStorageIsReused() => WithPipeAsync(async (server, client) =>
    {
        var request = new byte[9]; var status = new byte[36];
        using var stop = new CancellationTokenSource();
        using var io = new InputPipeStream(server, request, status, stop.Token);
        var first = io.ReadAsync(status, stop.Token);
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => ReadAttempt(io, status, stop.Token));
        await client.WriteAsync(new byte[36]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!first.IsCompleted) await Task.Delay(1, timeout.Token);
        Assert.Throws<InvalidOperationException>(() => WriteAttempt(io, request, stop.Token));
        Assert.Equal(36, first.Result);
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        var second = io.ReadAsync(status, stop.Token);
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(io.IsIoPending);
        Assert.ThrowsAny<OperationCanceledException>(() => ReadAttempt(io, status, stop.Token));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PendingWritesObserveCancellationAndPeerFailure(bool cancel) => WithPipeAsync(async (server, client) =>
    {
        var request = new byte[1024 * 1024]; var status = new byte[36];
        using var stop = new CancellationTokenSource();
        using var io = new InputPipeStream(server, request, status, stop.Token);
        var write = io.WriteAsync(request, stop.Token);
        Assert.False(write.IsCompleted);
        Assert.True(io.IsIoPending);
        if (cancel)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        else
        {
            client.Dispose();
            await Assert.ThrowsAsync<IOException>(() => write.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.False(io.IsIoPending);
    });

    [Fact]
    public async Task DisposalAndCompletionRaceWithoutFreeingActiveNativeStorage()
    {
        for (var round = 0; round < 16; round++) await WithPipeAsync(async (server, client) =>
        {
            var request = new byte[9]; var status = new byte[36];
            using var io = new InputPipeStream(server, request, status, default);
            var read = io.ReadAsync(status).AsTask();
            var dispose = Task.Run(io.Dispose);
            await Record.ExceptionAsync(() => client.WriteAsync(new byte[36]).AsTask());
            var failure = await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
            if (failure is not null) Assert.IsAssignableFrom<IOException>(failure);
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(io.CanRead); Assert.False(io.CanWrite);
            Assert.Throws<ObjectDisposedException>(() => ReadAttempt(io, status));
        });
    }

    [Fact]
    public Task DisposingAPendingReadDrainsItAndLeavesTheCallerPipeOpen() => WithPipeAsync(async (server, _) =>
    {
        var request = new byte[9]; var status = new byte[36];
        using var io = new InputPipeStream(server, request, status, default);
        var read = io.ReadAsync(status).AsTask();
        await Task.Run(io.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(server.CanWrite);
        io.Dispose();
    });

    [Fact]
    public Task WarmSynchronousAndSuspendedOwnedIoAllocateNoManagedObjects() => WithPipeAsync((server, client) =>
    {
        var request = new byte[9]; var status = new byte[36];
        using var io = new InputPipeStream(server, request, status, default);
        using var peer = new ReceiverPipeStream(client);
        using var done = new AutoResetEvent(false);
        Action completed = () => done.Set();
        for (var round = 0; round < 1000; round++) Frame();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 10000; round++) Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        return Task.CompletedTask;

        void Frame()
        {
            ConsumeCompleted(io.WriteAsync(request));
            if (peer.Read(peer.RequestBytes, 0, 9) != 9) throw new IOException("Request changed.");
            var read = io.ReadAsync(status);
            if (read.IsCompleted) throw new IOException("Expected a suspended read.");
            read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(completed);
            peer.Write(peer.StatusBytes, 0, 36);
            if (!done.WaitOne(5000) || read.Result != 36) throw new IOException("Reply changed.");
            peer.Write(peer.StatusBytes, 0, 36);
            var buffered = io.ReadAsync(status);
            if (!buffered.IsCompletedSuccessfully || buffered.Result != 36) throw new IOException("Buffered reply changed.");
        }
    });

    private static void ConsumeCompleted(ValueTask operation)
    {
        if (!operation.IsCompletedSuccessfully) throw new IOException("Expected a buffered write.");
        operation.GetAwaiter().GetResult();
    }

    private static void ReadAttempt(InputPipeStream io, Memory<byte> bytes, CancellationToken token = default)
        => _ = io.ReadAsync(bytes, token).AsTask();
    private static void WriteAttempt(InputPipeStream io, ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        => _ = io.WriteAsync(bytes, token).AsTask();

    private static int ReadCompleted(ValueTask<int> operation)
    {
        if (!operation.IsCompletedSuccessfully) throw new IOException("Expected completed IO.");
        return operation.Result;
    }

    [Fact]
    public Task AllocationFailuresAndInvalidOwnershipDoNotCloseTheCallerPipe() => WithPipeAsync((server, _) =>
    {
        var request = new byte[9]; var status = new byte[36];
        var failed = Assert.Throws<Win32Exception>(() => new InputPipeStream(server, request, status, default,
            (_, _) => { Marshal.SetLastPInvokeError(8); return IntPtr.Zero; }));
        Assert.Equal(8, failed.NativeErrorCode);
        Assert.Throws<IOException>(() => new InputPipeStream(server, request, status, default, (_, _) => throw new IOException("synthetic create failure")));
        using var io = new InputPipeStream(server, request, status, default);
        using var foreign = new CancellationTokenSource();
        Assert.Throws<ArgumentException>(() => ReadAttempt(io, new byte[36]));
        Assert.Throws<ArgumentException>(() => WriteAttempt(io, new byte[9]));
        Assert.Throws<ArgumentException>(() => ReadAttempt(io, status, foreign.Token));
        Assert.Equal(0, ReadCompleted(io.ReadAsync(status.AsMemory(0, 0))));
        ConsumeCompleted(io.WriteAsync(request.AsMemory(0, 0)));
        Assert.Throws<IOException>(() => InputPipeStream.ValidateWrite(8, 9));
        Assert.True(io.CanRead); Assert.True(io.CanWrite); Assert.False(io.CanSeek);
        Assert.Throws<NotSupportedException>(() => io.Length);
        Assert.Throws<NotSupportedException>(() => io.Position);
        Assert.Throws<NotSupportedException>(() => io.Position = 0);
        Assert.Throws<NotSupportedException>(() => io.Read(request, 0, 1));
        Assert.Throws<NotSupportedException>(() => io.Write(request, 0, 1));
        Assert.Throws<NotSupportedException>(() => io.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => io.SetLength(1));
        io.Flush();
        Assert.True(server.CanRead);
        return Task.CompletedTask;
    });

    [Fact]
    public void InvalidConstructionAndACollectedCallbackTargetAreSafe()
    {
        using var pipe = new NamedPipeServerStream("DesktopTwin.Invalid." + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentNullException>(() => new InputPipeStream(null!, new byte[9], new byte[36], default));
        Assert.Throws<ArgumentNullException>(() => new InputPipeStream(pipe, null!, new byte[36], default));
        Assert.Throws<ArgumentNullException>(() => new InputPipeStream(pipe, new byte[9], null!, default));
        Assert.Throws<ArgumentException>(() => new InputPipeStream(pipe, new byte[9], new byte[36], default));
        var context = GCHandle.Alloc(null, GCHandleType.Weak);
        try
        {
            var field = Assert.IsAssignableFrom<FieldInfo>(typeof(InputPipeStream).GetField("Callback", BindingFlags.Static | BindingFlags.NonPublic));
            var callback = Assert.IsAssignableFrom<Delegate>(field.GetValue(null));
            callback.DynamicInvoke(IntPtr.Zero, GCHandle.ToIntPtr(context), IntPtr.Zero, 0u);
        }
        finally { context.Free(); }
    }

    private static async Task WithPipeAsync(Func<NamedPipeServerStream, NamedPipeClientStream, Task> body)
    {
        var name = "DesktopTwin.HostIoTest." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 128, 128);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        await body(server, client);
    }
}
