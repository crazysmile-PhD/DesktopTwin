using System;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 区分 Windows 接受注销请求与会话真正消失。退出只等请求；需修改系统设置时等完成。
/// </summary>
internal sealed class SessionLogoffOperation
{
    /// <summary>注销请求的结果；成功下发不等于 Windows 已完成清理。</summary>
    internal Task<uint?> Request { get; }

    /// <summary>请求成功后，有界观察原会话消失。</summary>
    internal Task Completion { get; }

    /// <summary>建立同一注销请求的两个完成信号，不重复调用系统注销接口。</summary>
    internal SessionLogoffOperation(
        Task<uint?> request,
        Func<uint?> getCurrentSessionId,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Request = request;
        Completion = ObserveAsync(getCurrentSessionId, timeout, timeProvider, cancellationToken);
    }

    private async Task ObserveAsync(
        Func<uint?> getCurrentSessionId,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var sessionId = await Request.ConfigureAwait(false);
        if (sessionId is null) return;

        var startedAt = timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (getCurrentSessionId() != sessionId) return;
            if (timeProvider.GetElapsedTime(startedAt) >= timeout)
                throw new TimeoutException("Windows 仍在注销分身会话，请稍后重试。");
            await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
