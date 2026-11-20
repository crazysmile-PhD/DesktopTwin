using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopTwin.Services;

/// <summary>
/// 会话变更或串流端启动时纠正视角漂移；空闲时不查询。
/// 所有状态由 UI Dispatcher 管理；在途纠正退出前不启动下一轮。
/// </summary>
internal sealed class UuViewLockService : IDisposable
{
    private readonly Func<Action, Action<Exception>, IDisposable> _subscribe;
    private readonly Action<Action> _schedule;
    private readonly Func<UuViewTarget> _captureTarget;
    private readonly Func<UuViewTarget, bool> _needsCorrection;
    private readonly Func<UuViewTarget, CancellationToken, Task<UuViewSwitchService.Result>> _ensureTarget;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private UuViewSwitchService? _switchService;
    private bool _waitingForIdle;
    private bool _manualOperationActive;
    private int _attempts;
    private UuViewTarget? _target;
    private bool _switchMode;
    private CancellationTokenSource? _switchCts;
    private IDisposable? _subscription;
    private bool _checkPending;
    private bool _checkQueued;
    private bool _disposed;

    internal event EventHandler<Exception>? MonitoringFailed;

    internal UuViewLockService(UuViewSwitchService switchService, Dispatcher dispatcher)
        : this(UuViewSwitchService.CaptureTarget,
            UuViewSwitchService.NeedsCorrection, switchService.EnsureTargetAsync,
            (changed, failed) => new UuViewChangeMonitor(dispatcher, changed, failed),
            action => dispatcher.BeginInvoke(DispatcherPriority.Background, action))
    {
        _switchService = switchService;
        switchService.BecameIdle += OnOperationIdle;
    }

    internal UuViewLockService(
        Func<UuViewTarget> captureTarget,
        Func<UuViewTarget, bool> needsCorrection,
        Func<UuViewTarget, CancellationToken, Task<UuViewSwitchService.Result>> ensureTarget,
        Func<Action, Action<Exception>, IDisposable> subscribe,
        Action<Action> schedule,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _captureTarget = captureTarget;
        _needsCorrection = needsCorrection;
        _ensureTarget = ensureTarget;
        _subscribe = subscribe;
        _schedule = schedule;
        _delay = delay ?? Task.Delay;
    }

    internal void SetEnabled(bool enabled, bool switchEnabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 改热键会重复应用配置，不能把漂移后的位置当成新基线。
        if (enabled == _target.HasValue && switchEnabled == _switchMode)
        {
            return;
        }

        _checkPending = false;
        _waitingForIdle = false;
        _attempts = 0;
        _switchCts?.Cancel();
        _switchMode = switchEnabled;
        _target = null;
        if (!enabled)
        {
            _subscription?.Dispose();
            _subscription = null;
            return;
        }

        try
        {
            // 先记录，再在订阅完成后补查，覆盖启动监听期间发生的变化。
            var target = switchEnabled ? _captureTarget() : UuViewTarget.Host;
            _subscription ??= _subscribe(RequestCheck, OnMonitoringFailed);
            _target = target;
            AppLog.Info($"UU 视角锁定：记录目标为 {_target}。");
            RequestCheck();
        }
        catch
        {
            _subscription?.Dispose();
            _subscription = null;
            _target = null;
            throw;
        }
    }

    /// <summary>手动切换成功后更新基线；只开锁定时始终保持主机目标。</summary>
    internal void RebaseTo(UuViewTarget? target)
    {
        if (_disposed || _target is null || target is null
            || (!_switchMode && target != UuViewTarget.Host))
        {
            return;
        }

        _attempts = 0;
        _waitingForIdle = false;
        _switchCts?.Cancel();
        if (_target != target)
        {
            _target = target;
            AppLog.Info($"UU 视角锁定：记录目标更新为 {target}。");
        }

        RequestCheck();
    }

    /// <summary>合并一批通知；在途工作期间的通知留到收尾后再检查。</summary>
    internal void RequestCheck()
    {
        if (_disposed || _target is null)
        {
            return;
        }

        _checkPending = true;
        if (_switchCts is null && !_checkQueued && !_waitingForIdle && !_manualOperationActive)
        {
            _checkQueued = true;
            _schedule(async () =>
            {
                _checkQueued = false;
                if (_checkPending)
                {
                    await CheckAsync();
                }
            });
        }
    }

    private void OnOperationIdle(object? sender, EventArgs e) => _schedule(NotifyOperationIdle);

    /// <summary>用户操作优先：暂停事件纠正，取消在途纠正，结束后再统一观察。</summary>
    internal void SetManualOperationActive(bool active)
    {
        if (_disposed) return;
        _manualOperationActive = active;
        if (active) _switchCts?.Cancel();
        else NotifyOperationIdle();
    }

    /// <summary>忙碌请求保留到真正的操作空闲边界，不在 UI 上空转重试。</summary>
    internal void NotifyOperationIdle()
    {
        if (_disposed) return;
        _waitingForIdle = false;
        RequestCheck();
    }

    private void OnMonitoringFailed(Exception exception)
    {
        if (!_disposed && _target.HasValue)
        {
            SetEnabled(false, _switchMode);
            MonitoringFailed?.Invoke(this, exception);
        }
    }

    internal async Task CheckAsync()
    {
        if (_disposed || _manualOperationActive || _switchCts is not null || _target is not { } target)
        {
            return;
        }

        _checkPending = false;
        // CTS 归本轮所有。取消只发信号，不清引用、不释放，避免新旧两轮重叠。
        using var cts = new CancellationTokenSource();
        _switchCts = cts;
        try
        {
            if (!_needsCorrection(target))
            {
                _attempts = 0;
                return;
            }

            AppLog.Info($"UU 视角锁定：串流端偏离 {target}，自动切回。");
            var result = await _ensureTarget(target, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;
            if (result.Deferred)
            {
                _waitingForIdle = true;
                _checkPending = true;
                return;
            }
            _attempts++;
            if (!_needsCorrection(target))
            {
                _attempts = 0;
                _checkPending = false;
                return;
            }
            if (_attempts >= 3)
            {
                OnMonitoringFailed(new InvalidOperationException("连续三次纠正后目标仍不稳定，已停止自动切换。"));
                return;
            }
            if (!result.Success && !cts.IsCancellationRequested)
            {
                AppLog.Warn($"UU 视角锁定：第 {_attempts} 次纠正未成功（{result.Message}）。");
            }
            if (!result.Success || _checkPending)
            {
                _checkPending = false;
                await _delay(TimeSpan.FromSeconds(5 * _attempts), cts.Token).ConfigureAwait(true);
                _checkPending = true;
            }
            // 不从纠正结果更新基线，旧配置完成后也不会覆盖新配置。
        }
        catch (OperationCanceledException)
        {
            // 配置变化或退出时取消，正常路径。
        }
        catch (Exception exception)
        {
            AppLog.Warn("UU 视角锁定：自动切回出错。", exception);
            if (++_attempts >= 3) OnMonitoringFailed(exception);
        }
        finally
        {
            _switchCts = null;
            if (_checkPending && !_waitingForIdle)
            {
                RequestCheck();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _checkPending = false;
        _switchCts?.Cancel();
        _subscription?.Dispose();
        if (_switchService is { } switchService)
            switchService.BecameIdle -= OnOperationIdle;
        _switchService = null;
    }
}
