using System;
using System.Diagnostics;

namespace DesktopTwin.Services;

/// <summary>按阶段记录单调时钟耗时，用于区分应用工作与系统等待。</summary>
internal sealed class PerformanceTrace : IDisposable
{
    private readonly string _stage;
    private readonly Stopwatch _timer = Stopwatch.StartNew();

    /// <summary>开始测量指定阶段。</summary>
    internal PerformanceTrace(string stage) => _stage = stage;

    /// <summary>将本阶段耗时写入现有日志。</summary>
    public void Dispose() => AppLog.Info($"性能计时：{_stage} = {_timer.Elapsed.TotalMilliseconds:F0} ms");
}
