using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Models;
using DesktopTwin.Contracts;
using DrawingSize = System.Drawing.Size;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

/// <summary>
/// 桌面分身的编排层：负责会话生命周期、RDP 连接状态、失败重试与界面状态文本。
///
/// 视窗由应用入口注入；会话编排仅通过 UI 无关的端口操作它。
/// </summary>
public sealed class ChildSessionService : IDisposable
{
    private const int ErrorTimeout = 1460;

    private readonly SettingsService _settings;
    private readonly ISessionAutoLogin? _autoLogin;
    private readonly IChildSessionPlatform _platform;
    private readonly ISessionDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly SessionConnection _connection;
    private readonly SemaphoreSlim _launchSemaphore = new(1, 1);

    /// <summary>
    /// 正在跑的那次启动。两次启动叠在一起时共用它，避免各自做一遍预检、
    /// 再各自发起一次连接尝试（用户会看到两轮密码输入）。
    /// </summary>
    private Task? _startOperation;

    /// <summary>
    /// <see cref="_startOperation"/> 属于哪一代启动。
    ///
    /// 注销会推进代际，在途的那次启动随即作废（它回来时自己就放弃了）。这时候
    /// 新的启动不能去等那个注定不连接的旧任务，否则用户按下启动却什么也没发生。
    /// </summary>
    private int _startOperationGeneration = -1;

    /// <summary>
    /// 环境预检的代际：后开始的那一轮才算数，慢扫描不会把更新的结论盖回旧值。
    /// </summary>
    private int _environmentGeneration;

    /// <summary>
    /// 已经发布过的最大代际。用它而不是「等于当前代际」来判断：
    /// 只要求「不比已发布的旧」，慢扫描回来的结果照样能发布，不会被默默丢掉；
    /// 而更新的一轮稍后到达时仍然会覆盖它——顺序不会倒过来。
    /// </summary>
    private int _environmentPublishedGeneration;

    private readonly object _environmentGate = new();
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();
    private CancellationTokenSource _sessionCancellation = new();
    private bool _sessionClosing;
    private Task? _logoffOperation;
    private Task<uint?>? _logoffRequest;
    private bool _exitRequested;
    private SessionViewState? _publishedState;
    private long? _environmentCheckedTick;

    private readonly record struct SessionViewState(string Text, uint? SessionId, int Connection,
        bool Visible, bool Topmost, bool SmartSizing, bool Shortcuts,
        bool Muted, bool WatchMode, bool RelativeMouse, DrawingSize Size);

    /// <summary>
    /// 窗口是不是用户主动隐藏的。
    ///
    /// 视角切换里凡是要「确保窗口显示」的地方都要先看这个：用户点「隐藏」
    /// 就是不想看见它，切回分身时突然弹出来会打断他正在做的事。
    /// 反过来，切换过程中为了挂后台而最小化的窗口，不受这个标志约束。
    /// </summary>
    private bool _windowHiddenByUser;

    private readonly DesktopSessionHost _desktopHost;
    private bool _autoLaunchPending;

    private ChildSessionConnectionFailedEventArgs? _lastConnectionFailure;

    /// <summary>
    /// 最近一次连接失败像是「凭据被拒」。成功登录或开始新一轮尝试时清掉。
    /// </summary>
    private bool _credentialFailureSuspected;

    private RdpSettingChange _pendingRdpSettingChanges;

    private bool _disposed;
    private string? _lastOperationMessage;
    private SessionEnvironment _environment = SessionEnvironment.Empty;

    /// <summary>
    /// 启动代际。主动断开、注销与释放使它自增，旧启动据此判断是否已被取代。
    /// 预检被移到后台后，启动流程中间多了一个等待点，等待期间用户完全可能点了注销。
    /// </summary>
    private int _startGeneration;

    /// <summary>
    /// 界面需要刷新的状态发生变化。
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// RDP 连接失败或被意外断开。
    /// </summary>
    public event EventHandler<ChildSessionConnectionFailedEventArgs>? ConnectionFailed;

    /// <summary>
    /// 「系统组合键发送位置」切换后的自动重连已完成。
    /// </summary>
    public event EventHandler? SystemShortcutsReconnectCompleted;

    /// <summary>
    /// 音频静音切换后的自动重连已完成。
    /// </summary>
    public event EventHandler? AudioReconnectCompleted;

    /// <summary>
    /// 因为目标程序要求管理员权限，自动启动被跳过，需要用户手动确认。
    /// </summary>
    public event EventHandler? AutoLaunchSkipped;

    /// <summary>
    /// 分身分辨率被修改。全屏模式据此重新判断该用 1:1 还是自适应缩放。
    /// </summary>
    public event EventHandler? DesktopSizeChanged;

    /// <summary>
    /// 状态栏文本。
    /// </summary>
    public string StatusText { get; private set; } = Locale.Get("Main.Status.NotStarted");

    /// <summary>
    /// 分身窗口当前是否可见。
    /// </summary>
    public bool IsDesktopVisible => _desktopHost.Current?.IsVisible == true;

    /// <summary>
    /// RDP ActiveX 的连接状态：0 未连接、1 已连接、2 正在连接。
    /// </summary>
    public int ConnectedState { get; private set; }

    /// <summary>
    /// 当前分身会话号，未创建时为 null。
    /// </summary>
    public uint? ChildSessionId { get; private set; }

    /// <summary>
    /// 系统组合键是否发送到分身。
    /// </summary>
    public bool SendSystemShortcutsToRemote { get; private set; }

    /// <summary>
    /// 窗口是否置顶。
    /// </summary>
    public bool TopmostEnabled => _settings.Current.Session.Topmost;

    /// <summary>
    /// 画面是否自适应窗口。
    /// </summary>
    public bool SmartSizingEnabled => _settings.Current.Session.SmartSizing;

    /// <summary>
    /// 分身声音是否关闭。
    /// </summary>
    public bool AudioMuted => _settings.Current.Session.AudioMuted;

    /// <summary>是否启用独立的会话内游戏鼠标。</summary>
    public bool RelativeMouseModeEnabled => _settings.Current.Session.RelativeMouseMode;

    /// <summary>
    /// 是否处于观看模式。
    /// </summary>
    public bool WatchModeEnabled => _settings.Current.Session.WatchMode;

    /// <summary>
    /// 分身桌面的逻辑分辨率。
    /// </summary>
    public DrawingSize DesktopSize => _settings.Current.Session.AutomaticDesktopSize
        && _desktopHost.Current?.AvailableDesktopSize is { } available
        ? available
        : new(_settings.Current.Session.DesktopWidth, _settings.Current.Session.DesktopHeight);

    /// <summary>实际回报的远端尺寸，尚无回报时使用当前请求尺寸。</summary>
    public DrawingSize RenderedDesktopSize => _desktopHost.Current?.ReportedDesktopSize ?? DesktopSize;

    /// <summary>启用按窗口调整桌面分辨率，保留固定分辨率供手动切换。</summary>
    public void UseAutomaticDesktopSize()
    {
        ThrowIfDisposed();
        _settings.Update(s => s with { Session = s.Session with { AutomaticDesktopSize = true, SmartSizing = true } });
        DesktopSizeChanged?.Invoke(this, EventArgs.Empty);
        RefreshState(Locale.Get("Svc.Status.AutomaticResolution"));
    }

    private void OnRdpDesktopSizeChanged(object? sender, EventArgs e) =>
        DesktopSizeChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 普通窗口模式的记忆位置。
    /// </summary>
    public WindowPosition? NormalWindowPosition
    {
        get => _settings.Current.Window.NormalPosition;
        set
        {
            _settings.Update(s => s with { Window = s.Window with { NormalPosition = value } });
        }
    }

    /// <summary>
    /// 小窗模式的记忆位置。
    /// </summary>
    public WindowPosition? SmallWindowPosition
    {
        get => _settings.Current.Window.SmallPosition;
        set
        {
            _settings.Update(s => s with { Window = s.Window with { SmallPosition = value } });
        }
    }

    /// <summary>
    /// 最近一次启动前环境预检是否发现了可能影响桌面分身的环境设置。
    /// </summary>
    public bool HasEnvironmentIssue => _environment.HasIssues;

    /// <summary>
    /// 环境预检中是否存在必然失败的阻塞项。
    /// </summary>
    public bool HasBlockingEnvironmentIssue => _environment.HasBlockingIssues;

    /// <summary>
    /// 最近一次失败是否像是「凭据被拒」。
    ///
    /// 诊断面板据此补一条「密码可能不正确」的结论；成功登录或开始新一轮尝试后自动作废。
    /// </summary>
    public bool CredentialFailureSuspected => _credentialFailureSuspected;

    /// <summary>
    /// 作废「凭据被拒」的推断。用户重设密码后调用，避免面板上那条提示赖着不走。
    /// </summary>
    public void ClearCredentialFailureSuspected() => _credentialFailureSuspected = false;

    /// <summary>
    /// 最近一次启动前环境预检的结论文本，供界面提示与问题排查使用。
    /// </summary>
    public string EnvironmentIssueSummary => _environment.Summary;

    /// <summary>Composes a lifecycle with explicit platform, UI-thread and time dependencies.</summary>
    internal ChildSessionService(SettingsService settings,
        Func<ChildSessionService, IDesktopSessionView> createView,
        IChildSessionPlatform platform, ISessionDispatcher dispatcher, TimeProvider timeProvider,
        ISessionAutoLogin? autoLogin = null)
    {
        _settings = settings;
        _platform = platform;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _autoLogin = autoLogin;
        _desktopHost = new DesktopSessionHost(() => createView(this), () => settings.Current.Session,
            () => _autoLogin?.GetCredential());
        _desktopHost.VisibilityChanged += OnDesktopWindowVisibilityChanged;
        _desktopHost.ConnectionFailed += OnRdpConnectionFailed;
        _desktopHost.LoginCompleted += OnRdpLoginCompleted;
        _desktopHost.ConnectionStateChanged += OnRdpConnectionStateChanged;
        _desktopHost.DesktopSizeChanged += OnRdpDesktopSizeChanged;
        _connection = new SessionConnection(dispatcher, timeProvider,
            () => EnsureDesktopWindow().RdpHost, () => DesktopSize,
            () => _platform.TryGetChildSessionId() is { } id && _platform.IsSessionActive(id),
            CreateConnectionTimeoutFailure);
        _connection.Failed += (_, error) => CompleteConnectionFailure(error);
        _connection.Retrying += OnConnectionRetrying;
        SendSystemShortcutsToRemote = settings.Current.Session.SendSystemShortcutsToRemote;
        RefreshState();
    }

    /// <summary>
    /// 当前是否存在分身会话。
    /// </summary>
    public bool HasActiveChildSession()
    {
        RefreshState();
        return ChildSessionId is not null;
    }

    /// <summary>
    /// 启动桌面分身：先做一次环境预检，确保 Child Sessions 已启用，然后建立 RDP 连接并等待登录完成。
    /// 已经处于已连接状态时直接返回。
    /// </summary>
    public Task StartAsync() => StartAsync(null);

    /// <summary>
    /// 启动桌面分身，并要求「期间没有被注销取代」。
    /// </summary>
    /// <param name="expectedGeneration">
    /// 调用方在等待之前记下的 <see cref="StartGeneration"/>。用户在等待期间关掉了桌面分身，
    /// 这个值就对不上了——此时不能再往下走，否则会把刚关掉的会话又建回来。
    /// </param>
    public Task StartAsync(int expectedGeneration) => StartAsync((int?)expectedGeneration);

    private Task StartAsync(int? expectedGeneration)
    {
        ThrowIfDisposed();
        return _dispatcher.InvokeAsync(() => StartSharedAsync(expectedGeneration));
    }

    /// <summary>
    /// 当前启动代际。主动断开、注销或释放会推进它，调用方在等待前捕获并回传。
    /// </summary>
    public int StartGeneration
    {
        get
        {
            lock (_environmentGate)
            {
                return _startGeneration;
            }
        }
    }

    /// <summary>注销或退出时取消；调用方必须在操作开始时捕获，不可在等待后重新取。</summary>
    internal CancellationToken SessionCancellationToken
    {
        get { lock (_environmentGate) return _sessionCancellation.Token; }
    }

    private void InvalidateSessionOperations()
    {
        CancellationTokenSource previous;
        lock (_environmentGate)
        {
            _startGeneration++;
            previous = _sessionCancellation;
            _sessionCancellation = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
        _connection.Cancel();
    }

    private Task StartSharedAsync(int? expectedGeneration)
    {
        if (_disposed || _sessionClosing || (expectedGeneration is { } expected && expected != StartGeneration))
            return Task.CompletedTask;
        if (_startOperation is { IsCompleted: false } running && _startOperationGeneration == StartGeneration)
            return running;
        // Only the dispatcher writes this pair. A completed operation needs no asynchronous cleanup.
        _startOperationGeneration = StartGeneration;
        return _startOperation = StartCoreAsync(expectedGeneration);
    }

    private async Task StartCoreAsync(int? expectedGeneration)
    {
        ThrowIfDisposed();
        using var timing = new PerformanceTrace("启动分身（预检至登录结果，含重试/取消）");

        // 等待期间被注销（用户关掉了桌面分身）：直接放弃，不能把它再建回来。
        if (expectedGeneration is { } expected && expected != StartGeneration)
        {
            AppLog.Info("启动请求已过期（期间桌面分身被关闭），放弃。");
            return;
        }

        var startGeneration = _startGeneration;

        RefreshState();
        if (ConnectedState == 1 && ChildSessionId is { } connectedId
            && _platform.IsSessionActive(connectedId)) return;

        // 连接只做必要预检；计划任务与系统文件完整性留给完整诊断。
        //
        // 结果先放在局部变量里：被注销取代的那次启动不能把结果写进共享字段，
        // 否则会覆盖掉新一次启动刚发布的结论。
        int environmentGeneration;
        lock (_environmentGate)
        {
            environmentGeneration = ++_environmentGeneration;
        }

        // 已有会话的重连不重复跑完整诊断；新建前仍采集一次。
        var issues = ChildSessionId is null
            && (_environmentCheckedTick is not { } checkedAt || _timeProvider.GetElapsedTime(checkedAt) > TimeSpan.FromSeconds(30))
            ? await _platform.CollectConnectionIssuesAsync().ConfigureAwait(true)
            : _environment;

        // 预检期间可能被注销或释放。那之后不能再改系统状态、更不能把会话建回来。
        if (_disposed || _sessionClosing || startGeneration != _startGeneration)
        {
            AppLog.Info("环境预检期间启动已被注销或释放取代，放弃后续步骤。");
            return;
        }

        // 这里已经在 UI 线程上，发布是同步完成的。
        _ = PublishEnvironmentIssuesIfCurrent(issues, environmentGeneration);
        EnsureChildSessionsEnabled();
        RefreshState();

        if (ConnectedState == 1 && ChildSessionId is { } activeId
            && _platform.IsSessionActive(activeId))
        {
            return;
        }

        _lastConnectionFailure = null;
        _credentialFailureSuspected = false;
        await ConnectCoreAsync(Locale.Get("Svc.Status.Starting"));
    }

    /// <summary>
    /// 显示分身窗口。窗口不存在时创建。
    /// </summary>
    public void ShowWindow()
    {
        ThrowIfDisposed();
        _windowHiddenByUser = false;
        ShowDesktopWindow(EnsureDesktopWindow());
        RefreshState();
    }

    /// <summary>
    /// 隐藏分身窗口，RDP 连接保持不变。
    /// </summary>
    public void HideWindow()
    {
        ThrowIfDisposed();
        _windowHiddenByUser = true;
        _desktopHost.Current?.Hide();
        RefreshState(Locale.Get("Svc.Status.Hidden"));
    }

    /// <summary>
    /// 向分身发送 Win+D 显示桌面。
    /// </summary>
    public void ShowChildSessionDesktop()
    {
        ThrowIfDisposed();
        // 用户主动点了这个菜单，等于要把窗口叫回来。
        _windowHiddenByUser = false;
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendShowDesktopShortcut();
        RefreshState(Locale.Get("Svc.Status.SentWinD"));
    }

    /// <summary>
    /// 向分身发送 Win+Tab 打开任务视图。
    /// </summary>
    public void ShowChildSessionTaskView()
    {
        ThrowIfDisposed();
        // 同上：主动点了菜单，等于要把窗口叫回来。
        _windowHiddenByUser = false;
        var window = EnsureDesktopWindow();
        ShowDesktopWindow(window);
        window.RdpHost.SendTaskViewShortcut();
        RefreshState(Locale.Get("Svc.Status.SentWinTab"));
    }

    /// <summary>
    /// 切换画面「自适应缩放 / 1:1」。
    /// </summary>
    public void SetSmartSizing(bool enabled, bool persist = true)
    {
        ThrowIfDisposed();
        EnsureDesktopWindow().RdpHost.SetSmartSizing(enabled);

        // 全屏模式会临时强制自适应，退出时要还原用户原本的选择，
        // 因此这条路径不能写设置，否则用户的选择会被悄悄改掉。
        if (persist)
        {
            _settings.Update(s => s with { Session = s.Session with { SmartSizing = enabled } });
        }

        RefreshState(enabled ? Locale.Get("Svc.Status.Adaptive") : Locale.Get("Svc.Status.OneToOne"));
    }

    /// <summary>
    /// 切换窗口置顶。
    /// </summary>
    public void SetTopmost(bool enabled)
    {
        ThrowIfDisposed();
        _settings.Update(s => s with { Session = s.Session with { Topmost = enabled } });
        RefreshState(enabled ? Locale.Get("Svc.Status.Topmost") : Locale.Get("Svc.Status.NotTopmost"));
    }

    /// <summary>
    /// 切换系统组合键的发送位置。需要重连 RDP 才生效。
    /// </summary>
    /// <returns>是否触发了自动重连。</returns>
    public bool SetSendSystemShortcutsToRemote(bool enabled)
    {
        ThrowIfDisposed();
        if (SendSystemShortcutsToRemote == enabled)
        {
            return false;
        }

        SendSystemShortcutsToRemote = enabled;
        _settings.Update(s => s with { Session = s.Session with { SendSystemShortcutsToRemote = enabled } });
        var window = EnsureDesktopWindow();
        window.RdpHost.SetSendSystemShortcutsToRemote(enabled);
        var target = enabled ? Locale.Get("Svc.Target.Child") : Locale.Get("Svc.Target.Local");
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.SystemShortcuts,
            Locale.Format("Svc.Status.SystemShortcuts", target));
    }

    /// <summary>
    /// 切换分身声音。需要重连 RDP 才生效。
    /// </summary>
    /// <returns>是否触发了自动重连。</returns>
    public bool SetAudioMuted(bool muted)
    {
        ThrowIfDisposed();
        if (AudioMuted == muted)
        {
            return false;
        }

        _settings.Update(s => s with { Session = s.Session with { AudioMuted = muted } });
        var window = EnsureDesktopWindow();
        window.RdpHost.SetAudioMuted(muted);
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.Audio,
            muted ? Locale.Get("Svc.Status.AudioOff") : Locale.Get("Svc.Status.AudioOn"));
    }

    /// <summary>
    /// 切换独立的会话内输入接收端；不改变 RDP 连接或图形配置。
    /// </summary>
    public void SetRelativeMouseMode(bool enabled)
    {
        ThrowIfDisposed();
        if (RelativeMouseModeEnabled == enabled)
        {
            return;
        }

        _settings.Update(s => s with { Session = s.Session with { RelativeMouseMode = enabled } });
        RefreshState(enabled ? Locale.Get("Svc.Status.RelativeMouseOn") : Locale.Get("Svc.Status.RelativeMouseOff"));
    }

    /// <summary>
    /// 切换观看模式：开启后本机键鼠不会进入分身。
    /// </summary>
    public void SetWatchModeEnabled(bool enabled)
    {
        ThrowIfDisposed();
        _settings.Update(s => s with { Session = s.Session with { WatchMode = enabled } });
        // 切回控制模式时 SetInputBlocked 会把焦点还给输入窗口：观看模式期间
        // 被系统移走的焦点不会自动回来，画面看着像还在观看模式。
        _desktopHost.Current?.RdpHost.SetInputBlocked(enabled);
        RefreshState(enabled
            ? Locale.Get("Svc.Status.WatchModeOn")
            : Locale.Get("Svc.Status.WatchModeOff"));
    }

    /// <summary>
    /// 切换分身桌面分辨率。需要重连 RDP 才生效。
    /// </summary>
    public void SetDesktopSize(int width, int height)
    {
        ThrowIfDisposed();
        if (!SessionSettings.IsValidDesktopSize(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                Locale.Format(
                    "Svc.Error.ResolutionRange",
                    SessionSettings.MinDesktopWidth,
                    SessionSettings.MinDesktopHeight,
                    SessionSettings.MaxDesktopWidth,
                    SessionSettings.MaxDesktopHeight));
        }

        _settings.Update(s => s with
        {
            Session = s.Session with { AutomaticDesktopSize = false, DesktopWidth = width, DesktopHeight = height }
        });

        // 分辨率变了，全屏下「1:1 还是自适应」的判定前提也变了，通知界面重新算。
        DesktopSizeChanged?.Invoke(this, EventArgs.Empty);

        var window = EnsureDesktopWindow();
        if (window.RdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            RefreshState(Locale.Format("Svc.Status.ResolutionSetNext", width, height));
            return;
        }

        // 走和音频/快捷键同一套「带重试的设置重连」：单独直连一次失败就直接判死，
        // 还会把诊断面板弹到用户脸上——而实际只是重连的时机太早，重试一次就好。
        ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.Resolution,
            Locale.Format("Svc.Status.ResolutionApplied", width, height));
    }

    /// <summary>
    /// 在分身中启动当前配置的目标程序。
    /// </summary>
    public Task LaunchTargetAsync()
    {
        ThrowIfDisposed();
        var launch = _settings.Current.Launch;
        if (string.IsNullOrWhiteSpace(launch.TargetPath))
        {
            throw new InvalidOperationException(Locale.Get("Svc.Error.NoTargetProgram"));
        }

        return LaunchProgramCoreAsync(
            launch.TargetPath,
            launch.TargetArguments,
            launch.RunAsAdministrator,
            isAutomatic: false);
    }

    /// <summary>
    /// 在分身中启动指定程序。
    /// </summary>
    public Task LaunchProgramAsync(string executablePath, bool runAsAdministrator)
    {
        ThrowIfDisposed();
        return LaunchProgramCoreAsync(executablePath, string.Empty, runAsAdministrator, isAutomatic: false);
    }

    /// <summary>
    /// 断开 RDP、注销分身会话并隐藏窗口。
    /// </summary>
    /// <param name="expectedSessionId">需要注销的预期会话标识；为空时注销当前分身会话。</param>
    public Task LogoffAndHideAsync(uint? expectedSessionId = null)
    {
        ThrowIfDisposed();
        if (!_dispatcher.CheckAccess())
            return _dispatcher.InvokeAsync(() => LogoffAndHideAsync(expectedSessionId));
        if (_logoffOperation is { IsCompleted: false } running) return running;
        _logoffRequest = null;
        return _logoffOperation = LogoffCoreAsync(expectedSessionId);
    }

    /// <summary>
    /// 退出只等待 Windows 接受注销请求，系统继续清理；不等待最多 15 秒的完成观察。
    /// 如果已有注销在途，共用它的下发信号，不能再发送第二次注销。
    /// </summary>
    internal async Task RequestLogoffForExitAsync()
    {
        ThrowIfDisposed();
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(RequestLogoffForExitAsync);
            return;
        }

        _exitRequested = true;
        var completion = LogoffAndHideAsync();
        // 完成观察可能仍在运行。退出路径负责消费它的异常，避免留下未观察的失败任务。
        _ = ObserveExitLogoffAsync(completion);
        await (_logoffRequest is { } request ? request : completion);
    }

    private static async Task ObserveExitLogoffAsync(Task completion)
    {
        try { await completion; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppLog.Warn("退出时未确认分身注销完成。", exception); }
    }

    private async Task LogoffCoreAsync(uint? expectedSessionId)
    {
        ThrowIfDisposed();
        using var timing = new PerformanceTrace("分身注销（下发与完成观察）");
        var activeSessionId = _platform.TryGetChildSessionId();
        if (expectedSessionId is { } expected && activeSessionId is { } active && active != expected)
            throw new InvalidOperationException(Locale.Format("Svc.Status.SessionChanged", expected, active));

        // 注销不排在任务计划程序 COM 后面；先撤销所有连接/启动意图。
        _sessionClosing = true;
        InvalidateSessionOperations();
        try
        {
            _autoLaunchPending = false;
            _pendingRdpSettingChanges = RdpSettingChange.None;

            TryDisconnectRdpHost();
            RefreshState(Locale.Get("Svc.Status.LoggingOff"));

            _autoLogin?.StopWatching();
            _logoffRequest = (expectedSessionId ?? activeSessionId) is { } targetSessionId
                ? _platform.RequestLogoffAsync(targetSessionId) : Task.FromResult<uint?>(null);
            var operation = new SessionLogoffOperation(_logoffRequest,
                _platform.TryGetChildSessionId, TimeSpan.FromSeconds(15),
                _timeProvider, _disposeCancellationTokenSource.Token);
            await operation.Completion;
            var terminatedSessionId = await operation.Request;
            _desktopHost.Current?.Hide();
            RefreshState(terminatedSessionId is null
                ? Locale.Get("Svc.Status.NoSessionHidden")
                : Locale.Format("Svc.Status.SessionLoggedOff", terminatedSessionId.Value));
        }
        finally
        {
            _sessionClosing = _exitRequested;
        }
    }

    /// <summary>
    /// 关闭 Windows 的 Child Sessions 功能。属于高级操作，界面会二次确认。
    /// </summary>
    public void DisableChildSessionsFeature()
    {
        ThrowIfDisposed();
        _platform.DisableChildSessions();
        RefreshState(Locale.Get("Svc.Status.ChildSessionsDisabled"));
    }

    /// <summary>
    /// 刷新状态文本与连接状态。
    /// </summary>
    /// <param name="operationMessage">覆盖显示的最近操作说明。</param>
    public void RefreshState(string? operationMessage = null)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(operationMessage))
        {
            _lastOperationMessage = operationMessage;
        }

        try
        {
            var enabled = _platform.IsChildSessionsEnabled();
            ChildSessionId = _platform.TryGetChildSessionId();
            ConnectedState = _desktopHost.Current?.RdpHost.ConnectedState ?? 0;

            var connectionText = ConnectedState switch
            {
                0 => Locale.Get("Svc.Conn.None"),
                1 => Locale.Get("Svc.Conn.Connected"),
                2 => Locale.Get("Svc.Conn.Connecting"),
                _ => Locale.Format("Svc.Conn.Unknown", ConnectedState)
            };
            var sessionText = ChildSessionId?.ToString(CultureInfo.InvariantCulture) ?? Locale.Get("Check.None");
            var mainText = _lastOperationMessage ?? connectionText;
            var enabledText = enabled ? Locale.Get("Svc.Status.On") : Locale.Get("Svc.Status.Off");

            // 服务正常时不占用状态栏；只有「没启动」或「被禁用」才追加一段，
            // 让用户在点启动之前就能看出是环境问题、而不是连接问题。
            StatusText = Locale.Format("Svc.Status.Bar", mainText, connectionText, sessionText, enabledText)
                         + DescribeTermServiceForStatus();
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            StatusText = exception.GetBaseException().Message;
        }

        var state = new SessionViewState(StatusText, ChildSessionId, ConnectedState, IsDesktopVisible,
            TopmostEnabled, SmartSizingEnabled, SendSystemShortcutsToRemote,
            AudioMuted, WatchModeEnabled, RelativeMouseModeEnabled, DesktopSize);
        if (_publishedState != state)
        {
            _publishedState = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 状态栏里描述远程桌面服务的那一段：正常返回空串，异常时返回「 | TermService：未启动/已禁用」。
    /// </summary>
    private string DescribeTermServiceForStatus()
    {
        var (state, startType) = _platform.QueryTermService();

        if (startType == "Disabled")
        {
            return Locale.Get("Svc.Status.TermDisabled");
        }

        return state is "Stopped" or "StopPending" ? Locale.Get("Svc.Status.TermStopped") : string.Empty;
    }

    /// <summary>Asynchronously starts a stopped service; disposal cancels only the bounded observation.</summary>
    public Task<bool> TryStartTermServiceIfStoppedAsync() =>
        _platform.TryStartTermServiceIfStoppedAsync(_disposeCancellationTokenSource.Token);

    /// <summary>
    /// 采集并发布一次连接预检（全部阻塞项，不含完整诊断扫描）。
    ///
    /// 返回的 Task 在结果**真正发布之后**才完成：调用方紧接着就要读
    /// <see cref="HasBlockingEnvironmentIssue"/> 做启动决策，发布若只是排进队列
    /// （BeginInvoke 立刻返回），读到的还是上一次的结论，可能漏掉新的阻塞项。
    /// 采集由服务放到后台，调用方直接 await；发布仍切回 UI Dispatcher。
    /// </summary>
    public async Task RefreshEnvironmentCheckAsync()
    {
        ThrowIfDisposed();

        // 代际在「开始采集」时就定下：晚开始的那一轮才算数。
        int generation;
        lock (_environmentGate)
        {
            generation = ++_environmentGeneration;
        }

        using var timing = new PerformanceTrace("连接必要预检");
        var issues = await _platform.CollectConnectionIssuesAsync().ConfigureAwait(false);
        await PublishEnvironmentIssuesIfCurrent(issues, generation).ConfigureAwait(false);
    }

    private Task PublishEnvironmentIssuesIfCurrent(
        SessionEnvironment issues, int generation) =>
        _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            lock (_environmentGate)
            {
                if (generation < _environmentPublishedGeneration) return;
                _environmentPublishedGeneration = generation;
            }
            PublishEnvironmentIssues(issues);
        });

    /// <summary>
    /// 发布一次预检结果（字段 + 日志）。调用方必须已经在 UI 线程上。
    /// </summary>
    private void PublishEnvironmentIssues(SessionEnvironment issues)
    {
        _environment = issues;
        _environmentCheckedTick = _timeProvider.GetTimestamp();
        if (issues.HasIssues) AppLog.Warn($"环境预检：{issues.Summary}");
    }

    /// <summary>
    /// 释放资源并注销分身会话。应用退出时调用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        InvalidateSessionOperations();

        _disposed = true;
        _connection.Dispose();
        _autoLogin?.StopWatching();
        _disposeCancellationTokenSource.Cancel();
        _autoLaunchPending = false;
        _pendingRdpSettingChanges = RdpSettingChange.None;

        if (_desktopHost.Current is not null)
        {
            _desktopHost.VisibilityChanged -= OnDesktopWindowVisibilityChanged;
            _desktopHost.ConnectionFailed -= OnRdpConnectionFailed;
            _desktopHost.LoginCompleted -= OnRdpLoginCompleted;
            _desktopHost.ConnectionStateChanged -= OnRdpConnectionStateChanged;
            _desktopHost.DesktopSizeChanged -= OnRdpDesktopSizeChanged;
            TryDisconnectRdpHost();
        }

        try
        {
            // 应用正在退出，注销失败不应阻止主程序关闭；但会话残留会占着第二个桌面，必须尝试。
            // 带上当前会话号：若期间会话已被重建，宁可残留也不要误注销别人的会话。
            if (!_exitRequested || _logoffRequest is not { IsCompletedSuccessfully: true })
            {
                if (_platform.TryGetChildSessionId() is { } sessionId)
                    // OnExit cannot leave a request queued behind process teardown. As before,
                    // this fallback waits for request acceptance, never for Windows session cleanup.
                    _platform.RequestLogoffAsync(sessionId).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            AppLog.Warn("退出时注销分身会话失败。", exception);
        }

        _desktopHost.Dispose();

        // 在途 COM 启动可能仍会释放此信号量；不用 Dispose 制造退出竞态。
        _sessionCancellation.Cancel();
        _sessionCancellation.Dispose();
        _disposeCancellationTokenSource.Dispose();
    }

    private Task ConnectCoreAsync(string operationMessage)
    {
        var existingSessionId = _platform.TryGetChildSessionId();
        var window = EnsureDesktopWindow();
        if (!_windowHiddenByUser) ShowDesktopWindow(window);
        _autoLaunchPending = existingSessionId is null
            && _settings.Current.Launch.AutoLaunchOnConnect
            && !string.IsNullOrWhiteSpace(_settings.Current.Launch.TargetPath);
        _lastOperationMessage = operationMessage;
        var task = _connection.Start(reconnect: window.RdpHost.ConnectedState != 0);
        RefreshState();
        return task;
    }

    private IDesktopSessionView EnsureDesktopWindow() => _desktopHost.GetOrCreate();

    private static void ShowDesktopWindow(IDesktopSessionView window) => window.ShowAndActivate();

    private void OnRdpConnectionStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            RefreshState();
            if (_connection.Phase == SessionConnectionPhase.Connecting && ConnectedState == 2
                && _autoLogin is { IsEnabled: true }
                && !_connection.SuppressCredentialDialogFill)
            {
                _autoLogin.StartWatching();
            }
            else
            {
                _autoLogin?.StopWatching();
            }
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(exception.GetBaseException().Message);
        }
    }

    private async Task LaunchProgramCoreAsync(
        string executablePath,
        string arguments,
        bool runAsAdministrator,
        bool isAutomatic)
    {
        var generation = StartGeneration;
        var cancellationToken = SessionCancellationToken;
        if (_sessionClosing) return;
        try
        {
            await _launchSemaphore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            if (_disposed || _sessionClosing || generation != StartGeneration) return;
            var childSessionId = GetRequiredChildSessionId();

            var fileName = Path.GetFileName(executablePath);
            RefreshState(isAutomatic
                ? Locale.Format("Svc.Status.ReadyAutoLaunching", fileName)
                : Locale.Format("Svc.Status.Launching", fileName));
            await _platform.LaunchAsync(
                childSessionId,
                executablePath,
                arguments,
                runAsAdministrator, cancellationToken);
            if (_disposed || generation != StartGeneration) return;
            RefreshState(
                Locale.Format("Svc.Status.Launched", childSessionId, fileName)
                + (runAsAdministrator ? Locale.Get("Svc.Status.LaunchedElevated") : string.Empty));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 注销撤销排队或尚未下发的启动，不再向新会话发布结果。
        }
        catch (Exception error) when (IsExpectedChildSessionException(error))
        {
            if (!_disposed && generation == StartGeneration)
                RefreshState(Locale.Format("Svc.Status.LaunchUnconfirmed", error.Message));
            throw;
        }
        finally
        {
            _launchSemaphore.Release();
        }
    }

    /// <summary>
    /// 取当前分身会话 ID；没有可用会话时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    private uint GetRequiredChildSessionId()
    {
        var childSessionId = _platform.TryGetChildSessionId();
        if (childSessionId is null)
        {
            throw new InvalidOperationException(Locale.Get("Svc.Error.NoSession"));
        }

        return childSessionId.Value;
    }

    private void EnsureChildSessionsEnabled()
    {
        if (!_platform.IsChildSessionsEnabled())
        {
            _platform.EnableChildSessions();
        }
    }

    private void OnDesktopWindowVisibilityChanged(object? sender, EventArgs e)
    {
        RefreshState();
    }

    private void OnRdpConnectionFailed(object? sender, ChildSessionConnectionFailedEventArgs error)
    {
        if (_disposed || _sessionClosing) return;
        _lastConnectionFailure = error;
        _autoLogin?.StopWatching();
        _connection.ReportFailure(error);
    }

    // RDP 报告连接成功后稍作等待，让新分身会话的桌面初始化完成。
    // 使用 OnLoginComplete 等待实际登录完成，避免依赖固定时长的延时。
    private async void OnRdpLoginCompleted(object? sender, EventArgs e)
    {
        if (_disposed || _sessionClosing || !_connection.CompleteLogin()) return;
        var generation = StartGeneration;
        _lastConnectionFailure = null;
        _credentialFailureSuspected = false;
        _platform.ClearConnectionFailure();

        // 登录已经完成，再盯着凭据弹窗只会误填下一个（比如用户手动打开的安全对话框）。
        _autoLogin?.StopWatching();

        // 输入窗口每次连接都会新建，观看模式需要重新施加到新窗口上。
        _desktopHost.Current?.RdpHost.SetInputBlocked(WatchModeEnabled);

        var completedSettingChanges = _pendingRdpSettingChanges;
        if (completedSettingChanges != RdpSettingChange.None)
        {
            _pendingRdpSettingChanges = RdpSettingChange.None;
            RefreshState(Locale.Get("Svc.Status.RdpSettingsApplied"));

            if (completedSettingChanges.HasFlag(RdpSettingChange.SystemShortcuts))
            {
                SystemShortcutsReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }

            if (completedSettingChanges.HasFlag(RdpSettingChange.Audio))
            {
                AudioReconnectCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            RefreshState(Locale.Get("Svc.Status.LoginInitialized"));
        }

        if (!_autoLaunchPending)
        {
            return;
        }

        _autoLaunchPending = false;
        await Task.Yield();
        if (_disposed || _sessionClosing || generation != StartGeneration) return;

        try
        {
            RefreshState();
            if (ConnectedState == 1 && ChildSessionId is not null)
            {
                var launch = _settings.Current.Launch;
                if (!string.IsNullOrWhiteSpace(launch.TargetPath))
                {
                    if (launch.RunAsAdministrator)
                    {
                        // 自动 + 提权：必须有「用户针对这个目标程序亲自确认过」的授权。
                        // 授权与路径/参数绑定，改了目标就得重新确认——不能只凭 settings.json
                        // 里的一个 true 就在管理员上下文里执行任意程序。
                        if (ElevatedLaunchConsent.IsValid(
                                launch.ElevatedAutoLaunchConsent,
                                launch.TargetPath,
                                launch.TargetArguments))
                        {
                            await LaunchProgramCoreAsync(
                                launch.TargetPath,
                                launch.TargetArguments,
                                runAsAdministrator: true,
                                isAutomatic: true);
                        }
                        else
                        {
                            RefreshState(Locale.Get("Svc.Status.AutoLaunchSkippedAdmin"));
                            AutoLaunchSkipped?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else if (!_platform.IsLeastPrivilegeEnforced())
                    {
                        // 安全边界：UAC 关闭时 TASK_RUNLEVEL_LUA 不降权，普通权限的目标程序
                        // 仍然以高完整性运行，等于变相提权。这里判断的是「实际会不会降权」
                        // 而不是 RunAsAdministrator 标志，只看标志会被绕过去。
                        RefreshState(Locale.Get("Svc.Status.AutoLaunchSkippedNoUac"));
                        AutoLaunchSkipped?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        await LaunchProgramCoreAsync(
                            launch.TargetPath,
                            launch.TargetArguments,
                            runAsAdministrator: false,
                            isAutomatic: true);
                    }
                }
            }
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(Locale.Format("Svc.Status.AutoLaunchFailed", exception.GetBaseException().Message));
        }
    }

    private void OnConnectionRetrying(int number, int maximum, TimeSpan delay, bool settingsChange)
    {
        _autoLogin?.StopWatching();
        AppLog.Warn($"RDP 连接将在 {delay.TotalSeconds:0} 秒后重试（第 {number} 次）。");
        RefreshState(Locale.Format(settingsChange ? "Svc.Status.RdpReconnectRetrying" : "Svc.Status.InitialRetrying",
            delay.TotalSeconds, number, maximum));
    }

    /// <summary>
    /// 连接失败的统一收尾：「重试耗尽」与「连接超时」两条路径都经由这里附加诊断信息，
    /// 再更新状态并通知订阅者。
    /// </summary>
    private void CompleteConnectionFailure(ChildSessionConnectionFailedEventArgs e)
    {
        _autoLogin?.StopWatching();
        // 统一在这里补充诊断，让「重试耗尽」和「连接超时」两条失败路径给出同样的信息。
        var diagnosed = new ChildSessionConnectionFailedEventArgs(
            $"{e.Message}{BuildFailureDiagnosis()}",
            e.ErrorCode,
            e.ExtendedErrorCode);
        _lastConnectionFailure = diagnosed;
        _autoLaunchPending = false;
        _credentialFailureSuspected = IsLikelyCredentialFailure(diagnosed);

        // 留给「复制环境信息」：环境全绿却连不上时，这一行通常才是关键。
        // 用 e.Message 而不是 diagnosed.Message：后者带上了整段环境诊断，不适合塞进一行。
        _platform.RecordConnectionFailure(
            e.ErrorCode,
            e.ExtendedErrorCode,
            e.Message);

        AppLog.Error(
            $"分身 RDP 连接失败：{diagnosed.Message}，错误代码：{diagnosed.ErrorCode}，"
            + $"扩展错误代码：{diagnosed.ExtendedErrorCode}");
        RefreshState(diagnosed.Message);
        ConnectionFailed?.Invoke(this, diagnosed);
    }

    /// <summary>
    /// 这次失败像不像「凭据被拒」。
    ///
    /// RDP 在凭据被拒时只回报「发生了内部错误」(1) 并把连接断开，没有专用错误码；
    /// 反过来说，超时、客户端致命错误（内存不足 / 窗口创建失败 / 无效状态等）
    /// 和「取消登录」这类事件都能明确排除。剩下的一律按「可能」，措辞上也留了余地。
    /// </summary>
    private static bool IsLikelyCredentialFailure(ChildSessionConnectionFailedEventArgs e)
    {
        // 2 内存不足、3 窗口创建失败、5 无效状态、6/7 不可恢复、100 套接字初始化失败。
        if (e.ErrorCode is 2 or 3 or 5 or 6 or 7 or 100 or ErrorTimeout)
        {
            return false;
        }

        // 负数来自 OnLogonError：-3 及以下是取消/无权限一类的交互事件，不是密码问题。
        if (e.ErrorCode <= -3)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 构造连接超时的失败信息，并附上 RDP ActiveX 最后报告的失败原因（如果有）。
    /// </summary>
    private ChildSessionConnectionFailedEventArgs CreateConnectionTimeoutFailure(SessionConnectionPhase phase, TimeSpan budget)
    {
        var key = phase switch
        {
            SessionConnectionPhase.Disconnecting => "Svc.Error.DisconnectTimeout",
            SessionConnectionPhase.WaitingForRelease => "Svc.Error.ReleaseTimeout",
            _ => "Svc.Error.Timeout"
        };
        var timeoutMessage = Locale.Format(key, budget.TotalSeconds);
        var lastDiagnostic =
            _desktopHost.Current?.RdpHost.LastConnectionDiagnostic
            ?? _lastConnectionFailure;
        if (lastDiagnostic is null)
        {
            return new ChildSessionConnectionFailedEventArgs(
                Locale.Format("Svc.Error.Timeout.NoDetail", timeoutMessage),
                ErrorTimeout);
        }

        return new ChildSessionConnectionFailedEventArgs(
            Locale.Format("Svc.Error.Timeout.LastReport", timeoutMessage, lastDiagnostic.Message),
            lastDiagnostic.ErrorCode,
            lastDiagnostic.ExtendedErrorCode);
    }

    /// <summary>
    /// 连接失败后的补充诊断：报告是否存在分身会话，并附上环境预检结论。
    /// RDP ActiveX 自身只回报「发生内部错误」和断开原因，单独看这些信息无法定位问题。
    /// </summary>
    private string BuildFailureDiagnosis()
    {
        var builder = new StringBuilder();

        if (_platform.TryGetChildSessionId() is null)
        {
            builder.AppendLine().AppendLine();
            builder.Append(
                Locale.Get("Svc.Diag.NoSession"));
        }

        if (_environment.HasIssues)
        {
            builder.AppendLine().AppendLine();
            builder.Append(_environment.Summary);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 尝试断开 RDP 主机连接。ActiveX 正在自行断开时可能返回 COM 错误，忽略后仍继续注销会话。
    /// </summary>
    private void TryDisconnectRdpHost()
    {
        try
        {
            _desktopHost.Current?.RdpHost.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException)
        {
            // ActiveX 正在自行断开时可能返回 COM 错误，仍可继续注销分身会话。
        }
    }

    private static bool IsExpectedChildSessionException(Exception exception)
    {
        return exception is Win32Exception
            or COMException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
            or EntryPointNotFoundException
            or TargetInvocationException
            or InvalidCastException
            or InvalidOperationException
            or TimeoutException
            or IOException
            or ArgumentException;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool ReconnectForRdpSettingChange(
        IDesktopSessionView window, RdpSettingChange settingChange, string operationMessage)
    {
        if (_sessionClosing) return false;
        RefreshState();
        _pendingRdpSettingChanges |= settingChange;
        if (window.RdpHost.ConnectedState == 0 && ChildSessionId is null && !_connection.IsActive)
        {
            RefreshState(Locale.Format("Svc.Status.SettingAppliedNextConnect", operationMessage));
            return false;
        }
        _autoLogin?.StopWatching();
        _lastOperationMessage = Locale.Format("Svc.Status.SettingAppliedReconnecting", operationMessage);
        _ = _connection.Start(reconnect: true, settingsChange: true);
        RefreshState();
        return true;
    }

    [Flags]
    private enum RdpSettingChange
    {
        None = 0,
        SystemShortcuts = 1,
        Audio = 2,
        Resolution = 4
    }
}
