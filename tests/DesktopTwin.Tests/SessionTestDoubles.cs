using System.Drawing;
using System.IO;
using DesktopTwin.Contracts;
using DesktopTwin.Models;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>Only explicit advancement fires timers; no wall-clock sleeps or dispatcher threads.</summary>
internal sealed class ManualSessionTime : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
    internal int TimerCount => _timers.Count(timer => !timer.Disposed && timer.Due != long.MaxValue);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Advance(TimeSpan amount)
    {
        var target = _timestamp + amount.Ticks;
        while (true)
        {
            var next = _timers.Where(timer => !timer.Disposed && timer.Due <= target).MinBy(timer => timer.Due);
            if (next is null) break;
            _timestamp = next.Due;
            next.Due = next.Period > 0 ? _timestamp + next.Period : long.MaxValue;
            next.Fire();
        }
        _timestamp = target;
    }

    private sealed class ManualTimer(ManualSessionTime owner, TimerCallback callback, object? state) : ITimer
    {
        internal long Due { get; set; }
        internal long Period { get; private set; }
        internal bool Disposed { get; private set; }
        internal void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Disposed) return false;
            Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._timestamp + dueTime.Ticks;
            Period = period.Ticks;
            return true;
        }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class TestSessionDispatcher : ISessionDispatcher
{
    private readonly Queue<Action> _posted = [];
    internal int QueuedCount => _posted.Count;
    internal int InvokeCalls { get; private set; }
    internal bool Access { get; set; } = true;
    public bool CheckAccess() => Access;
    public Task InvokeAsync(Func<Task> action) { InvokeCalls++; return action(); }
    public Task InvokeAsync(Action action) { InvokeCalls++; action(); return Task.CompletedTask; }
    public void Post(Action action) => _posted.Enqueue(action);
    internal void Drain()
    {
        while (_posted.TryDequeue(out var action)) action();
    }
}

internal sealed class FakeSessionView : IDesktopSessionView
{
    internal FakeRdpClient Client { get; } = new();
    internal int CloseCalls { get; private set; }
    internal int ShowCalls { get; private set; }
    public bool IsVisible { get; private set; }
    public Size? AvailableDesktopSize { get; set; }
    public Size? ReportedDesktopSize { get; set; }
    public IRdpSessionClient RdpHost => Client;
    public event EventHandler? VisibilityChanged;
    public event EventHandler? DesktopSizeChanged;
    public void ShowAndActivate() { ShowCalls++; IsVisible = true; VisibilityChanged?.Invoke(this, EventArgs.Empty); }
    public void Hide() { IsVisible = false; VisibilityChanged?.Invoke(this, EventArgs.Empty); }
    public void Close() { CloseCalls++; Hide(); }
    internal void Resize(Size size) { ReportedDesktopSize = size; DesktopSizeChanged?.Invoke(this, EventArgs.Empty); }
}

internal sealed class FakeRdpClient : IRdpSessionClient
{
    internal List<Size> Connections { get; } = [];
    internal int DisconnectCalls { get; private set; }
    internal bool DisconnectImmediately { get; set; } = true;
    internal bool RemoteShortcuts { get; private set; }
    internal bool AudioMuted { get; private set; }
    internal bool InputBlocked { get; private set; }
    internal Exception? ConnectError { get; set; }
    internal Exception? DisconnectError { get; set; }
    internal Action? OnConnect { get; set; }
    internal Func<(string UserName, string Domain, string Password)?>? CredentialProvider { get; private set; }
    public int ConnectedState { get; set; }
    public ChildSessionConnectionFailedEventArgs? LastConnectionDiagnostic { get; private set; }
    public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;
    public event EventHandler? LoginCompleted;
    public void SetCredentialProvider(Func<(string UserName, string Domain, string Password)?>? provider) => CredentialProvider = provider;
    public void SetSmartSizing(bool enabled) { }
    public void SetSendSystemShortcutsToRemote(bool enabled) => RemoteShortcuts = enabled;
    public void SetAudioMuted(bool muted) => AudioMuted = muted;
    public void SetInputBlocked(bool enabled) => InputBlocked = enabled;
    public void ConnectToChildSession(Size size)
    {
        Connections.Add(size);
        if (ConnectError is not null) throw ConnectError;
        ConnectedState = 2;
        OnConnect?.Invoke();
    }
    public void DisconnectSession()
    {
        DisconnectCalls++;
        if (DisconnectError is not null) throw DisconnectError;
        if (DisconnectImmediately) ConnectedState = 0;
    }
    public void SendShowDesktopShortcut() { }
    public void SendTaskViewShortcut() { }
    internal void Login() { ConnectedState = 1; LoginCompleted?.Invoke(this, EventArgs.Empty); }
    internal void Fail(int code = 5)
    {
        LastConnectionDiagnostic = new ChildSessionConnectionFailedEventArgs("failed", code);
        ConnectionFailed?.Invoke(this, LastConnectionDiagnostic);
    }
}

internal sealed class FakeSessionPlatform : IChildSessionPlatform
{
    internal uint? SessionId { get; set; }
    internal bool SessionActive { get; set; }
    internal bool Enabled { get; private set; }
    internal bool LeastPrivilege { get; set; } = true;
    internal int EnableCalls { get; private set; }
    internal int CollectionCalls { get; private set; }
    internal int Queries { get; private set; }
    internal List<uint> Logoffs { get; } = [];
    internal List<(uint Session, string Path, bool Elevated)> Launches { get; } = [];
    internal Task<SessionEnvironment>? Collection { get; set; }
    internal Task<uint?>? LogoffRequest { get; set; }
    internal Task? Launch { get; set; }
    internal CancellationToken LaunchCancellation { get; private set; }
    public bool IsChildSessionsEnabled() => Enabled;
    public uint? TryGetChildSessionId() { Queries++; return SessionId; }
    public bool IsSessionActive(uint sessionId) => SessionActive;
    public void EnableChildSessions() { EnableCalls++; Enabled = true; }
    public void DisableChildSessions() => Enabled = false;
    public (string State, string StartType) QueryTermService() => ("Running", "Manual");
    public Task<bool> TryStartTermServiceIfStoppedAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<SessionEnvironment> CollectConnectionIssuesAsync()
    {
        CollectionCalls++;
        return Collection ?? Task.FromResult(SessionEnvironment.Empty);
    }
    public Task<uint?> RequestLogoffAsync(uint sessionId)
    {
        Logoffs.Add(sessionId);
        if (LogoffRequest is not null) return LogoffRequest;
        SessionId = null;
        SessionActive = false;
        return Task.FromResult<uint?>(sessionId);
    }
    public Task LaunchAsync(uint sessionId, string path, string arguments, bool elevated, CancellationToken cancellationToken)
    {
        LaunchCancellation = cancellationToken;
        Launches.Add((sessionId, path, elevated));
        return Launch ?? Task.CompletedTask;
    }
    public bool IsLeastPrivilegeEnforced() => LeastPrivilege;
    public void RecordConnectionFailure(int error, int? extendedError, string message) { }
    public void ClearConnectionFailure() { }
}

internal sealed class FakeSessionAutoLogin : ISessionAutoLogin
{
    internal int Starts { get; private set; }
    internal int Stops { get; private set; }
    public bool IsEnabled => true;
    public (string UserName, string Domain, string Password)? GetCredential() => ("test", "domain", "synthetic");
    public void StartWatching() => Starts++;
    public void StopWatching() => Stops++;
}

internal sealed class SessionTestRig : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "session-lifecycle", Guid.NewGuid().ToString("N"));
    internal ManualSessionTime Time { get; } = new();
    internal TestSessionDispatcher Dispatcher { get; } = new();
    internal FakeSessionPlatform Platform { get; } = new();
    internal FakeSessionView View { get; } = new();
    internal FakeSessionAutoLogin AutoLogin { get; } = new();
    internal SettingsService Settings { get; }
    internal ChildSessionService Service { get; }
    internal SessionTestRig()
    {
        Directory.CreateDirectory(_directory);
        Settings = new SettingsService(_directory);
        Service = new ChildSessionService(Settings, _ => View, Platform, Dispatcher, Time, AutoLogin);
    }
    internal void Advance(TimeSpan amount) { Time.Advance(amount); Dispatcher.Drain(); }
    public void Dispose()
    {
        Service.Dispose();
        Dispatcher.Drain();
        Settings.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
