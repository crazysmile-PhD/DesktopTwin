using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Services;

namespace DesktopTwin.Diagnostics;

/// <summary>只在命令行显式启用的当前用户命名管道；一条连接只执行一个有界请求。</summary>
internal sealed class LocalControlBridge : IDisposable
{
    private const int MaximumRequestBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NamedPipeServerStream _pipe;
    private readonly Func<DesktopControlRequest, CancellationToken, Task<object>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _disposed;

    internal LocalControlBridge(string pipeName, Func<DesktopControlRequest, CancellationToken, Task<object>> handle)
    {
        ValidatePipeName(pipeName);
        _handle = handle;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Current user SID is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        // CurrentUserOnly also compares elevation. An explicit user ACL permits the owner's
        // ordinary test client to reach this opt-in elevated application without a general broker.
        _pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        _loop = RunAsync();
        AppLog.Info($"本地测试桥接已启用：{pipeName}；仅当前用户，拒绝网络登录令牌。");
    }

    internal static string? ReadPipeArgument(string[] args)
    {
        var index = Array.IndexOf(args, "--control-pipe");
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException("--control-pipe requires a pipe name.");
        ValidatePipeName(args[index + 1]);
        return args[index + 1];
    }

    private static void ValidatePipeName(string value)
    {
        const string prefix = "DesktopTwin.Control.";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(value[prefix.Length..], "N", out _))
            throw new ArgumentException("Expected DesktopTwin.Control.<32 hex digits>.", nameof(value));
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestStop.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var response = await HandlePacketAsync(requestStop.Token).ConfigureAwait(false);
                    await WritePacketAsync(_pipe, JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                        requestStop.Token).ConfigureAwait(false);
                    // DisconnectNamedPipe can discard unread output. Wait for the client to
                    // acknowledge the complete reply, using the same deadline as the request.
                    var acknowledgement = new byte[1];
                    await _pipe.ReadExactlyAsync(acknowledgement, requestStop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                {
                    if (!_stop.IsCancellationRequested) AppLog.Warn("本地测试请求已中断。", error);
                }
                finally
                {
                    if (!_stop.IsCancellationRequested && _pipe.IsConnected) _pipe.Disconnect();
                }
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is (OperationCanceledException or ObjectDisposedException or IOException))
        {
            // Disposal cancels either the accept or the currently bounded request.
        }
        catch (Exception error)
        {
            AppLog.Error("本地测试桥接已停止。", error);
        }
    }

    private async Task<object> HandlePacketAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadPacketAsync(_pipe, cancellationToken).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize<DesktopControlRequest>(bytes, JsonOptions)
                ?? throw new ArgumentException("Missing control request.");
            request.Validate();
            var result = await _handle(request, cancellationToken).ConfigureAwait(false);
            return new { ok = true, result };
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or Win32Exception)
        {
            return new { ok = false, error = error.Message };
        }
    }

    internal static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumRequestBytes) throw new ArgumentOutOfRangeException(nameof(stream), "Invalid packet length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    internal static async Task WritePacketAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _pipe.Dispose();
        _ = _loop.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
