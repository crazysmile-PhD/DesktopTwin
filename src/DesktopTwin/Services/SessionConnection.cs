using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Contracts;
using DesktopTwin.Models;

namespace DesktopTwin.Services;

/// <summary>The mutually exclusive phases of one bounded RDP connection operation.</summary>
internal enum SessionConnectionPhase { Idle, Disconnecting, WaitingForRelease, Connecting, RetryDelay }

/// <summary>
/// Owns the sole reconnect queue, retry budget and deadline. All transitions run on the session dispatcher;
/// timer callbacks carry a version so already queued callbacks cannot affect a replacement operation.
/// </summary>
internal sealed class SessionConnection(
    ISessionDispatcher dispatcher,
    TimeProvider timeProvider,
    Func<IRdpSessionClient> getClient,
    Func<Size> getSize,
    Func<bool> isSessionActive,
    Func<SessionConnectionPhase, TimeSpan, ChildSessionConnectionFailedEventArgs> timeoutFailure) : IDisposable
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private ITimer? _timer;
    private int _timerVersion;
    private long _phaseStarted;
    private int _retry;
    private bool _settingsChange;
    private bool _acceptDisconnectFailure;
    private bool _disposed;
    private TaskCompletionSource<bool>? _completion;

    internal SessionConnectionPhase Phase { get; private set; }
    internal bool IsActive => Phase != SessionConnectionPhase.Idle;
    internal bool SuppressCredentialDialogFill { get; private set; }
    internal event EventHandler<ChildSessionConnectionFailedEventArgs>? Failed;
    internal event Action<int, int, TimeSpan, bool>? Retrying;

    /// <summary>Joins the operation, coalescing settings into the latest size and configuration.</summary>
    internal Task Start(bool reconnect, bool settingsChange = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsActive)
        {
            if (settingsChange)
            {
                _settingsChange = true;
                _retry = 0;
                // Waiting already uses the latest settings. An in-flight Connect used the old ones.
                if (Phase == SessionConnectionPhase.Connecting) StartAttempt(reconnect: true);
            }
            return _completion?.Task ?? Task.CompletedTask;
        }

        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsChange = settingsChange;
        _retry = 0;
        SuppressCredentialDialogFill = false;
        _acceptDisconnectFailure = false;
        StartAttempt(reconnect);
        return _completion.Task;
    }

    private void StartAttempt(bool reconnect)
    {
        CancelTimer();
        try
        {
            if (reconnect)
            {
                Phase = SessionConnectionPhase.Disconnecting;
                _phaseStarted = timeProvider.GetTimestamp();
                getClient().DisconnectSession();
                PollDisconnect();
            }
            else Connect();
        }
        catch (Exception error)
        {
            var failure = new ChildSessionConnectionFailedEventArgs(error.GetBaseException().Message, error.HResult);
            if (Phase == SessionConnectionPhase.Connecting
                && error.GetBaseException() is not (ArgumentException or UnauthorizedAccessException)) ReportFailure(failure);
            else Fail(failure);
        }
    }

    private void PollDisconnect()
    {
        if (getClient().ConnectedState == 0)
        {
            Phase = SessionConnectionPhase.WaitingForRelease;
            _phaseStarted = timeProvider.GetTimestamp();
            PollRelease();
        }
        else if (timeProvider.GetElapsedTime(_phaseStarted) >= AttemptTimeout) Fail(timeoutFailure(Phase, AttemptTimeout));
        else Schedule(PollInterval, PollDisconnect);
    }

    private void PollRelease()
    {
        if (!isSessionActive()) Connect();
        else if (timeProvider.GetElapsedTime(_phaseStarted) >= ReleaseTimeout) Fail(timeoutFailure(Phase, ReleaseTimeout));
        else Schedule(PollInterval, PollRelease);
    }

    private void Connect()
    {
        Phase = SessionConnectionPhase.Connecting;
        // Set the timer before entering COM: Connect can synchronously report login or failure.
        Schedule(AttemptTimeout, () => Fail(timeoutFailure(SessionConnectionPhase.Connecting, AttemptTimeout)));
        getClient().ConnectToChildSession(getSize());
    }

    /// <summary>Accepts one failure per attempt; callbacks during teardown or backoff are ignored.</summary>
    internal void ReportFailure(ChildSessionConnectionFailedEventArgs error)
    {
        if (_disposed) return;
        if (Phase == SessionConnectionPhase.Idle)
        {
            if (_acceptDisconnectFailure) Fail(error);
            return;
        }
        if (Phase is SessionConnectionPhase.RetryDelay or SessionConnectionPhase.Disconnecting
            or SessionConnectionPhase.WaitingForRelease) return;
        var maximum = _settingsChange ? 3 : 1;
        if (_retry >= maximum) { Fail(error); return; }
        _retry++;
        Phase = SessionConnectionPhase.RetryDelay;
        SuppressCredentialDialogFill = !_settingsChange;
        var delay = TimeSpan.FromSeconds(_settingsChange ? 1 << _retry : 4);
        Schedule(delay, () => StartAttempt(reconnect: true));
        Retrying?.Invoke(_retry, maximum, delay, _settingsChange);
    }

    /// <summary>Only the currently connecting operation can publish login completion.</summary>
    internal bool CompleteLogin()
    {
        if (_disposed || Phase != SessionConnectionPhase.Connecting) return false;
        CancelTimer();
        Phase = SessionConnectionPhase.Idle;
        SuppressCredentialDialogFill = false;
        _acceptDisconnectFailure = true;
        _completion?.TrySetResult(true);
        return true;
    }

    private void Fail(ChildSessionConnectionFailedEventArgs error)
    {
        Cancel();
        // Disconnecting is best effort. A lost callback must not leave a live timed-out attempt.
        try { getClient().DisconnectSession(); }
        catch (Exception disconnectError) { AppLog.Warn("连接失败后断开 RDP 失败。", disconnectError); }
        Failed?.Invoke(this, error);
    }

    /// <summary>Cancels intent and completes its waiters without cancelling native Windows cleanup.</summary>
    internal void Cancel()
    {
        CancelTimer();
        Phase = SessionConnectionPhase.Idle;
        _acceptDisconnectFailure = false;
        _completion?.TrySetResult(false);
    }

    private void Schedule(TimeSpan delay, Action action)
    {
        CancelTimer();
        var version = _timerVersion;
        _timer = timeProvider.CreateTimer(_ => dispatcher.Post(() =>
        {
            if (_disposed || version != _timerVersion) return;
            try { action(); }
            catch (Exception error)
            {
                Fail(new ChildSessionConnectionFailedEventArgs(error.GetBaseException().Message, error.HResult));
            }
        }), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        _timerVersion++;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Cancel();
        _disposed = true;
    }
}
