using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using DesktopTwin.Input;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>Real bundled process/pipe tests; heartbeat leases never send input transitions to the desktop.</summary>
public sealed class RelativeInputReceiverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLaunchIsReportedWithoutWaitingForHandshake(bool connectWithoutHello)
    {
        var failure = new IOException("synthetic launch failure");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        NamedPipeClientStream? client = null;
        try
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                using var session = await RelativeInputSession.ConnectAsync((uint)Process.GetCurrentProcess().SessionId,
                    timeout.Token, async (_, arguments, token) =>
                    {
                        if (connectWithoutHello)
                        {
                            client = new NamedPipeClientStream(".", arguments.Split(' ')[1], PipeDirection.InOut, PipeOptions.Asynchronous);
                            await client.ConnectAsync(token);
                        }
                        throw failure;
                    });
            });
            Assert.Same(failure, error);
            Assert.False(timeout.IsCancellationRequested);
        }
        finally { client?.Dispose(); }
    }

    [Fact]
    public async Task BootstrapClosesFailedReceiverAndRetriesItsCreationReceiptOnlyOnce()
    {
        var receivers = new List<Process>();
        RelativeInputSession? session = null;
        try
        {
            session = await RelativeInputSession.ConnectAsync((uint)Process.GetCurrentProcess().SessionId, default,
                (path, arguments, token) => LaunchForBootstrapAsync(path, arguments, receivers,
                    attempt => attempt == 1 ? new SessionLaunchReceiptMismatchException("test receipt") : null, token));
            Assert.True(session.Ready);
            Assert.Equal(2, receivers.Count);
            await receivers[0].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(receivers[0].HasExited);
            Assert.False(receivers[1].HasExited);
        }
        finally
        {
            session?.Dispose();
            foreach (var receiver in receivers) { await StopAsync(receiver); receiver.Dispose(); }
        }
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task BootstrapHasABoundedRetryAndDoesNotRetryOtherLaunchFailures(bool receiptFailure, int expectedLaunches)
    {
        var receivers = new List<Process>();
        try
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                using var session = await RelativeInputSession.ConnectAsync((uint)Process.GetCurrentProcess().SessionId, default,
                    (path, arguments, token) => LaunchForBootstrapAsync(path, arguments, receivers,
                        _ => receiptFailure ? new SessionLaunchReceiptMismatchException("test receipt") : new IOException("test IO failure"), token));
            });
            if (receiptFailure) Assert.IsType<SessionLaunchReceiptMismatchException>(error);
            else Assert.IsType<IOException>(error);
            Assert.Equal(expectedLaunches, receivers.Count);
            foreach (var receiver in receivers) await receiver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { foreach (var receiver in receivers) { await StopAsync(receiver); receiver.Dispose(); } }
    }

    private static async Task LaunchForBootstrapAsync(string path, string arguments,
        List<Process> receivers, Func<int, Exception?> failure, CancellationToken token)
    {
        var receiver = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new IOException("Receiver did not start.");
        receivers.Add(receiver);
        var attempt = receivers.Count;
        await Task.Delay(500, token);
        if (failure(attempt) is { } error) throw error;
    }

    [Fact]
    public async Task RealReceiverReportsItsSessionAndExitsWhenHostDisconnects()
    {
        RelativeInputSession? session = null;
        Process? receiver = null;
        try
        {
            session = await RelativeInputSession.ConnectAsync((uint)Process.GetCurrentProcess().SessionId, default, (path, arguments, token) =>
            {
                receiver = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true });
                // Real first-launch verification can outlast the receiver's active input lease.
                return Task.Delay(2500, token);
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (session.Status is null) await Task.Delay(20, deadline.Token);
            Assert.True(session.Ready);
            Assert.Equal(Process.GetCurrentProcess().SessionId, session.Status.SessionId);
            Assert.Equal(0, session.Status.InjectedMoves);
            Assert.Equal(0, session.Status.HeldCount);
            var initialStatus = session.Status;
            // Longer than the active lease: an unarmed peer must survive without background exchanges.
            await Task.Delay(1800, deadline.Token);
            Assert.Same(initialStatus, session.Status);
            Assert.NotNull(receiver);
            Assert.False(receiver.HasExited);
            session.Dispose();
            Assert.NotNull(receiver);
            await receiver.WaitForExitAsync(deadline.Token);
            Assert.False(session.Ready);
        }
        finally
        {
            session?.Dispose();
            if (receiver is not null)
            {
                await StopAsync(receiver);
                receiver.Dispose();
            }
        }
    }

    [Fact]
    public async Task PassiveStatusRefreshDoesNotStartHeartbeatsAndCaptureRevocationReturnsToIdle()
    {
        RelativeInputSession? session = null;
        Process? receiver = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            session = await RelativeInputSession.ConnectAsync((uint)Process.GetCurrentProcess().SessionId, timeout.Token,
                (path, arguments, _) =>
                {
                    receiver = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true });
                    return Task.CompletedTask;
                });
            while (session.Status is null) await Task.Delay(10, timeout.Token);
            var beforeRefresh = session.Status;
            session.RequestStatus();
            await WaitForStatusChangeAsync(session, beforeRefresh, timeout.Token);
            var passive = session.Status;
            await Task.Delay(1800, timeout.Token);
            Assert.Same(passive, session.Status);
            Assert.True(session.Ready);

            // Authorize heartbeats only: this test never injects a mouse/key/button into the desktop.
            session.SetForegroundLease(true);
            await WaitForStatusChangeAsync(session, passive, timeout.Token);
            var active = session.Status;
            await WaitForStatusChangeAsync(session, active, timeout.Token);
            var previous = session.Status;
            session.SetForegroundLease(false);
            var changes = 0;
            var observation = Stopwatch.StartNew();
            while (observation.ElapsedMilliseconds < 1800)
            {
                var current = session.Status;
                if (!ReferenceEquals(previous, current)) { changes++; previous = current; }
                await Task.Delay(5, timeout.Token);
            }
            // One already in-flight active reply may precede the single revocation reply.
            // Continued heartbeats would exceed this bound during the observation window.
            Assert.InRange(changes, 1, 2);
            var idle = session.Status;
            Assert.True(session.Ready);
            Assert.NotNull(idle);
            Assert.Equal(0, idle.HeldCount);
            Assert.Equal(0, idle.InjectedMoves);
            Assert.NotNull(receiver);
            Assert.False(receiver.HasExited);
        }
        finally
        {
            session?.Dispose();
            if (receiver is not null) { await StopAsync(receiver); receiver.Dispose(); }
        }
    }

    private static async Task WaitForStatusChangeAsync(RelativeInputSession session,
        RelativeInputStatus? previous, CancellationToken cancellationToken)
    {
        while (ReferenceEquals(previous, session.Status)) await Task.Delay(5, cancellationToken);
    }

    [Fact]
    public async Task ReceiverStopsWhenTheHostLeaseExpiresEvenIfTheHostProcessIsAlive()
    {
        using var pipe = NewPipe(out var name);
        using var receiver = Start(name, Environment.ProcessId, Process.GetCurrentProcess().SessionId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            var hello = new byte[12];
            await pipe.ReadExactlyAsync(hello, timeout.Token);
            using var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            // Authorize a lease without sending any key/button/move; expiry still closes the channel.
            RelativeInputProtocol.WritePacket(writer, new(InputOperation.Heartbeat, 1));
            var status = new byte[RelativeInputProtocol.StatusBytes];
            await pipe.ReadExactlyAsync(status, timeout.Token);
            await receiver.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, receiver.ExitCode);
        }
        finally { await StopAsync(receiver); }
    }

    [Fact]
    public async Task ReceiverRejectsWrongParentAndWrongSessionBeforeAcceptingInput()
    {
        using var pipe = NewPipe(out var name);
        using var wrongSession = Start(name, Environment.ProcessId, Process.GetCurrentProcess().SessionId + 1);
        await wrongSession.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, wrongSession.ExitCode);
        using var wrongParent = Start(name, Environment.ProcessId + 1, Process.GetCurrentProcess().SessionId);
        await wrongParent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, wrongParent.ExitCode);
    }

    private static NamedPipeServerStream NewPipe(out string name)
    {
        name = "DesktopTwin.Input." + Guid.NewGuid().ToString("N");
        return new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
    private static Process Start(string name, int parent, int session)
    {
        var path = SessionLaunchChannel.EnsureEmbeddedProgram("DesktopTwin.InputReceiver.exe");
        return Process.Start(new ProcessStartInfo(path, $"--relative-input {name} {parent} {session}")
            { UseShellExecute = false, CreateNoWindow = true }) ?? throw new IOException("Receiver did not start.");
    }
    private static async Task StopAsync(Process receiver)
    {
        try { if (!receiver.HasExited) receiver.Kill(); }
        catch (InvalidOperationException) when (receiver.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (receiver.HasExited) { }
        await receiver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
