using System;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 合并同一界面发出的后台采集请求；采集期间的新请求使旧快照失效。
/// 入口与发布回调由调用方的 UI 上下文串行执行。
/// </summary>
internal sealed class AsyncSnapshotRefresh<T>(
    Func<Task<T>> collect, Action<T> publish, Action<Exception> failed) : IDisposable
{
    private Task _operation = Task.CompletedTask;
    private bool _running;
    private bool _pending;
    private bool _disposed;

    /// <summary>请求刷新；并发请求共享当前任务，最多保留一次后续采集。</summary>
    internal Task RequestAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _pending = true;
        if (!_running)
        {
            _running = true;
            _operation = RunAsync();
        }
        return _operation;
    }

    private async Task RunAsync()
    {
        try
        {
            while (_pending && !_disposed)
            {
                _pending = false;
                try
                {
                    var snapshot = await collect();
                    if (!_disposed && !_pending) publish(snapshot);
                }
                catch (Exception exception)
                {
                    if (!_disposed && !_pending) failed(exception);
                }
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>关闭后丢弃在途结果，不声称中止了底层采集。</summary>
    public void Dispose()
    {
        _disposed = true;
        _pending = false;
    }
}
