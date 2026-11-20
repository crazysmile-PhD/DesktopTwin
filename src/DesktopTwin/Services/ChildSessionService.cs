using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.Models;
using DesktopTwin.View;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

/// <summary>
/// 桌面分身的编排层：负责会话生命周期、RDP 连接状态、失败重试与界面状态文本。
///
/// 分层说明：本类直接持有 <see cref="MainWindow"/> 的引用。这是一个单窗口应用，
/// 为「一个窗口 + 一个会话」的强绑定关系引入额外抽象只会增加阅读成本，
/// 因此这里选择务实做法：服务负责状态与流程，窗口只负责显示。
/// </summary>
public sealed class ChildSessionService : IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 连接失败后等多久再试一次。
    ///
    /// 凭据被拒时 Windows 会直接把 RDP 断开，只有重新连接才会再次弹出凭据框，
    /// 所以「再给用户一次机会」在实现上就是「等几秒重连一次」。
    /// 4 秒给用户留出看清提示、重新输入的时间。
    /// </summary>
    private static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 一次启动最多再重试几次。
    ///
    /// 只留 1 次：每次重试都会再试一次密码，失败一次就消耗一次账户的锁定配额
    /// （默认阈值 10 次）。两次足够覆盖手滑，再多就是在拿用户账户冒险。
    /// </summary>
    private const int ConnectionRetryCount = 1;

    /// <summary>
    /// 设置切换后的自动重连次数。
    ///
    /// 断开之后立刻重连，经常被 RDS 用「访问被拒绝」顶回来（日志里几乎每次都复现），
    /// 隔几秒再连就能成功。重试不够时，用户看到的是「第一次必定失败、手动再点一次才行」，
    /// 所以这里给足三次（2s / 4s / 8s）。
    /// </summary>
    private const int RdpSettingsReconnectRetryCount = 3;
    private const int ErrorTimeout = 1460;

    private readonly SettingsService _settings;
    private readonly AutoLoginService? _autoLogin;
    private readonly DispatcherTimer _statusTimer;
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
    /// 保护 <see cref="_startOperation"/> 的赋值与清除：两者可能在不同线程上
    /// （清理由 await 的续体在池线程执行），不加锁会用一个「清空」把刚赋上的新任务抹掉，
    /// 下一次启动于是又各跑一遍预检、又发一次连接尝试。
    /// </summary>
    private readonly object _startOperationGate = new();

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
        bool Visible, bool Topmost, bool SmartSizing, bool AspectRatio, bool Shortcuts,
        bool Muted, bool WatchMode, bool RelativeMouse, DrawingSize Size);

    /// <summary>
    /// 窗口是不是用户主动隐藏的。
    ///
    /// 视角切换里凡是要「确保窗口显示」的地方都要先看这个：用户点「隐藏」
    /// 就是不想看见它，切回分身时突然弹出来会打断他正在做的事。
    /// 反过来，切换过程中为了挂后台而最小化的窗口，不受这个标志约束。
    /// </summary>
    private bool _windowHiddenByUser;

    /// <summary>
    /// 创建服务时所在的 UI 线程调度器。
    ///
    /// 视角切换里多处 await 用了 ConfigureAwait(false)，等到需要操作 RDP 控件时
    /// 已经在线程池上了；WPF 对象只认创建它的线程，直接碰就是
    /// 「调用线程无法访问此对象，因为另一个线程拥有该对象」。
    /// 涉及 UI 的入口统一用它切回去。
    /// </summary>
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private MainWindow? _desktopWindow;
    private bool _autoLaunchPending;
    private TaskCompletionSource<bool>? _connectionAttemptCompletionSource;
    private ChildSessionConnectionFailedEventArgs? _lastConnectionFailure;
    private int _initialConnectionRetriesRemaining;
    private bool _connectionRetryInProgress;

    /// <summary>
    /// 当前这一次连接尝试的超时截止时刻（<see cref="Environment.TickCount64"/>）。
    /// 重试会重新计算，否则总超时会把刚开始的重试直接掐掉。
    /// </summary>
    private long _attemptDeadlineTick;

    /// <summary>
    /// 最近一次连接失败像是「凭据被拒」。成功登录或开始新一轮尝试时清掉。
    /// </summary>
    private bool _credentialFailureSuspected;

    /// <summary>
    /// 本轮尝试是否不自动填凭据弹窗。
    ///
    /// 重试的唯一目的是让用户自己再输一次密码，自动填会把同一份密码再提交一遍——
    /// 错的还是错，只是又消耗一次失败登录。注意只关「填弹窗」这一路，
    /// 直接交给 RDP 的凭据（<see cref="ProvideAutoLoginCredential"/>）保持不动。
    /// </summary>
    private bool _suppressCredentialDialogFill;
    private RdpSettingChange _pendingRdpSettingChanges;

    /// <summary>
    /// 设置重连重试的代际号。切到主桌面时自增，让还在 <c>Task.Delay</c> 里等的
    /// 那次重试作废——否则它醒来后会把刚断开的分身又连回来。
    /// 只作废这一次重试，不清 <see cref="_pendingRdpSettingChanges"/>：
    /// 待应用的设置要留给下一次成功登录去收尾。
    /// </summary>
    private int _settingsReconnectGeneration;
    private int _rdpSettingsReconnectRetriesRemaining;
    private bool _rdpSettingsReconnectRetryInProgress;
    private bool _rdpSettingsReconnectActive;
    private bool _statusTickInProgress;
    private bool _disposed;
    private string? _lastOperationMessage;
    private System.Collections.Generic.IReadOnlyList<ChildSessionEnvironmentCheck.Issue> _environmentIssues = [];

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

    /// <summary>登录完成后报告鼠标设置的接受情况，包括初次连接与设置重连。</summary>
    public event EventHandler? RelativeMouseModeConfigured;

    /// <summary>
    /// 因为目标程序要求管理员权限，自动启动被跳过，需要用户手动确认。
    /// </summary>
    public event EventHandler? AutoLaunchSkipped;

    /// <summary>
    /// 分身分辨率被修改。全屏模式据此重新判断该用 1:1 还是自适应缩放。
    /// </summary>
    public event EventHandler? DesktopSizeChanged;

    /// <summary>
    /// 分身窗口实例。窗口在首次显示或首次连接时创建，未创建时为 null。
    /// </summary>
    public MainWindow? Window => _desktopWindow;

    /// <summary>
    /// 自动登录服务。未注入时为 null。
    ///
    /// 界面要读写「已保存的密码」，而凭据的实际使用发生在本服务内部，
    /// 与其让两处各持一个实例，不如由本服务持有、界面借用。
    /// </summary>
    internal AutoLoginService? AutoLogin => _autoLogin;

    /// <summary>
    /// 状态栏文本。
    /// </summary>
    public string StatusText { get; private set; } = Locale.Get("Main.Status.NotStarted");

    /// <summary>
    /// 分身窗口当前是否可见。
    /// </summary>
    public bool IsDesktopVisible => _desktopWindow?.IsVisible == true;

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
    /// 是否保持宽高比。
    /// </summary>
    public bool KeepAspectRatio => _settings.Current.Session.KeepAspectRatio;

    /// <summary>
    /// 分身声音是否关闭。
    /// </summary>
    public bool AudioMuted => _settings.Current.Session.AudioMuted;

    /// <summary>是否请求实验性原生相对鼠标。</summary>
    public bool RelativeMouseModeEnabled => _settings.Current.Session.RelativeMouseMode;

    /// <summary>最近一次连接的鼠标设置是否被控件接受，不代表游戏输入已验证。</summary>
    public bool RelativeMouseModeSettingsAccepted =>
        _desktopWindow?.RdpHost.RelativeMouseModeSettingsAccepted == true;

    /// <summary>最近一次连接配置时请求的鼠标模式，不含尚未应用的排队设置。</summary>
    internal bool RelativeMouseModeConfiguredEnabled =>
        _desktopWindow?.RdpHost.RelativeMouseModeConfiguredEnabled == true;

    /// <summary>
    /// 是否处于观看模式。
    /// </summary>
    public bool WatchModeEnabled => _settings.Current.Session.WatchMode;

    /// <summary>
    /// 分身桌面的逻辑分辨率。
    /// </summary>
    public DrawingSize DesktopSize => new(
        _settings.Current.Session.DesktopWidth,
        _settings.Current.Session.DesktopHeight);

    /// <summary>
    /// 普通窗口模式的记忆位置。
    /// </summary>
    public WindowPosition? NormalWindowPosition
    {
        get => _settings.Current.Window.NormalPosition;
        set
        {
            _settings.Current.Window.NormalPosition = value;
            _settings.SaveSoon();
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
            _settings.Current.Window.SmallPosition = value;
            _settings.SaveSoon();
        }
    }

    /// <summary>
    /// 系统是否已安装并启用 RDP Wrapper。它与桌面分身功能不兼容，界面会在启动前据此提示用户。
    /// </summary>
    public static bool IsRdpWrapperEnabled()
    {
        return ChildSessionNativeMethods.IsRdpWrapperEnabled();
    }

    /// <summary>
    /// 最近一次启动前环境预检是否发现了可能影响桌面分身的环境设置。
    /// </summary>
    public bool HasEnvironmentIssue => _environmentIssues.Count > 0;

    /// <summary>
    /// 环境预检中是否存在必然失败的阻塞项。
    /// </summary>
    public bool HasBlockingEnvironmentIssue =>
        ChildSessionEnvironmentCheck.HasBlockingIssue(_environmentIssues);

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
    public string EnvironmentIssueSummary =>
        ChildSessionEnvironmentCheck.BuildSummary(_environmentIssues);

    /// <summary>
    /// 创建服务。构造时立即刷新一次状态，界面随后可以安全读取。
    /// </summary>
    public ChildSessionService(SettingsService settings)
        : this(settings, autoLogin: null)
    {
    }

    /// <summary>
    /// 创建服务并注入自动登录。
    /// </summary>
    /// <param name="settings">设置来源。</param>
    /// <param name="autoLogin">自动登录服务；为空表示不启用自动登录。</param>
    internal ChildSessionService(SettingsService settings, AutoLoginService? autoLogin)
    {
        _settings = settings;
        _autoLogin = autoLogin;
        SendSystemShortcutsToRemote = settings.Current.Session.SendSystemShortcutsToRemote;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
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

        // 后台调用方
        // 调过来时已经在池线程上，而下面要碰 RDP 控件和窗口。WPF 对象只认创建它的
        // 线程，不切回去就直接抛 InvalidOperationException——兜底反而成了故障点。
        if (_dispatcher.CheckAccess())
        {
            return StartSharedAsync(expectedGeneration);
        }

        return _dispatcher
            .InvokeAsync(() => StartSharedAsync(expectedGeneration))
            .Task
            .Unwrap();
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
        _settingsReconnectGeneration++;
        _connectionRetryInProgress = false;
        _rdpSettingsReconnectRetryInProgress = false;
        _rdpSettingsReconnectActive = false;
    }

    /// <summary>
    /// 同一次启动只跑一遍：两次启动请求叠在一起时，
    /// 后到的那个等前一个的结果，不再单独发起一次连接尝试——
    /// 否则第一次尝试失败后，第二次的预检回来时会重新建一个尝试，用户要输两遍密码。
    /// </summary>
    private Task StartSharedAsync(int? expectedGeneration)
    {
        // 先校验请求，再决定是否共用任务，旧请求不能借用新一代的启动。
        if (_sessionClosing || (expectedGeneration is { } expected && expected != StartGeneration))
            return Task.CompletedTask;
        lock (_startOperationGate)
        {
            if (_startOperation is { IsCompleted: false } running
                && _startOperationGeneration == _startGeneration)
            {
                return running;
            }

            // 上一次启动已被注销作废：它的任务还在跑，但不会去连接，不能共用。
            var task = StartCoreAsync(expectedGeneration);
            _startOperation = task;
            _startOperationGeneration = _startGeneration;
            return AwaitAndClearStartAsync(task);
        }
    }

    private async Task AwaitAndClearStartAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_startOperationGate)
            {
                if (ReferenceEquals(_startOperation, task))
                {
                    _startOperation = null;
                }
            }
        }
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
            && ChildSessionNativeMethods.IsSessionActive(connectedId)) return;

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
            && (_environmentCheckedTick is not { } checkedAt || Environment.TickCount64 - checkedAt > 30_000)
            ? await Task.Run(() => ChildSessionEnvironmentCheck.CollectForConnection()).ConfigureAwait(true)
            : _environmentIssues;

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
            && ChildSessionNativeMethods.IsSessionActive(activeId))
        {
            return;
        }

        var completionSource = _connectionAttemptCompletionSource;
        if (completionSource is null || completionSource.Task.IsCompleted)
        {
            completionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionAttemptCompletionSource = completionSource;
            _lastConnectionFailure = null;
            _credentialFailureSuspected = false;

            // 先给整次启动一个超时预算：ConnectCore 里可能压根没有发起连接
            //（窗口已处于连接状态），那种情况下没有这一步就会立刻判超时。
            MarkAttemptStarted();

            try
            {
                ConnectCore(Locale.Get("Svc.Status.Starting"));
            }
            catch
            {
                _autoLaunchPending = false;
                _initialConnectionRetriesRemaining = 0;
                completionSource.TrySetResult(false);
                _connectionAttemptCompletionSource = null;
                throw;
            }
        }

        try
        {
            await WaitForConnectionAttemptAsync(completionSource);
        }
        finally
        {
            if (ReferenceEquals(_connectionAttemptCompletionSource, completionSource))
            {
                _connectionAttemptCompletionSource = null;
            }
        }
    }

    /// <summary>
    /// 等本次启动出结果。
    ///
    /// 超时按**单次尝试**计算：重试会把截止时刻推后，否则用户在第一轮慢慢输入密码，
    /// 重试刚开始就被总超时掐掉，日志还会误报成「连接超时」。
    /// 超时属于外部原因（会话没响应），不再重试——直接按失败处理。
    /// </summary>
    private async Task WaitForConnectionAttemptAsync(TaskCompletionSource<bool> completionSource)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            SessionCancellationToken, _disposeCancellationTokenSource.Token);
        while (!completionSource.Task.IsCompleted)
        {
            var remainingMilliseconds = _attemptDeadlineTick - Environment.TickCount64;
            if (remainingMilliseconds <= 0)
            {
                _autoLaunchPending = false;
                _initialConnectionRetriesRemaining = 0;
                TryDisconnectRdpHost();
                CompleteConnectionFailure(CreateConnectionTimeoutFailure());
                return;
            }

            try
            {
                await completionSource.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(remainingMilliseconds),
                    cancellation.Token);
            }
            catch (TimeoutException)
            {
                // 回到循环顶部重新读截止时刻：可能刚被重试推后了。
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 标记一次新的连接尝试开始，重置单次尝试的超时截止时刻。
    /// </summary>
    private void MarkAttemptStarted() =>
        _attemptDeadlineTick = Environment.TickCount64 + (long)ConnectionTimeout.TotalMilliseconds;

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
        _desktopWindow?.Hide();
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
            _settings.Current.Session.SmartSizing = enabled;
            _settings.SaveSoon();
        }

        RefreshState(enabled ? Locale.Get("Svc.Status.Adaptive") : Locale.Get("Svc.Status.OneToOne"));
    }

    /// <summary>
    /// 切换是否保持宽高比。
    /// </summary>
    public void SetKeepAspectRatio(bool enabled)
    {
        ThrowIfDisposed();
        _settings.Current.Session.KeepAspectRatio = enabled;
        _settings.SaveSoon();
        RefreshState(enabled ? Locale.Get("Svc.Status.KeepAspect") : Locale.Get("Svc.Status.NoAspect"));
    }

    /// <summary>
    /// 切换窗口置顶。
    /// </summary>
    public void SetTopmost(bool enabled)
    {
        ThrowIfDisposed();
        _settings.Current.Session.Topmost = enabled;
        _settings.SaveSoon();
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
        _settings.Current.Session.SendSystemShortcutsToRemote = enabled;
        _settings.SaveSoon();
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

        _settings.Current.Session.AudioMuted = muted;
        _settings.SaveSoon();
        var window = EnsureDesktopWindow();
        window.RdpHost.SetAudioMuted(muted);
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.Audio,
            muted ? Locale.Get("Svc.Status.AudioOff") : Locale.Get("Svc.Status.AudioOn"));
    }

    /// <summary>
    /// 切换实验性相对鼠标，沿用既有的有界设置重连流程。
    /// </summary>
    /// <returns>是否触发了自动重连。</returns>
    public bool SetRelativeMouseMode(bool enabled)
    {
        ThrowIfDisposed();
        if (RelativeMouseModeEnabled == enabled)
        {
            return false;
        }

        _settings.Current.Session.RelativeMouseMode = enabled;
        _settings.SaveSoon();
        var window = EnsureDesktopWindow();
        window.RdpHost.SetRelativeMouseMode(enabled);
        return ReconnectForRdpSettingChange(
            window,
            RdpSettingChange.RelativeMouse,
            enabled ? Locale.Get("Svc.Status.RelativeMouseOn") : Locale.Get("Svc.Status.RelativeMouseOff"));
    }

    /// <summary>
    /// 切换观看模式：开启后本机键鼠不会进入分身。
    /// </summary>
    public void SetWatchModeEnabled(bool enabled)
    {
        ThrowIfDisposed();
        _settings.Current.Session.WatchMode = enabled;
        _settings.SaveSoon();
        // 切回控制模式时 SetInputBlocked 会把焦点还给输入窗口：观看模式期间
        // 被系统移走的焦点不会自动回来，画面看着像还在观看模式。
        _desktopWindow?.RdpHost.SetInputBlocked(enabled);
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

        _settings.Current.Session.DesktopWidth = width;
        _settings.Current.Session.DesktopHeight = height;
        _settings.SaveSoon();

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
            return _dispatcher.InvokeAsync(() => LogoffAndHideAsync(expectedSessionId)).Task.Unwrap();
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
            await _dispatcher.InvokeAsync(RequestLogoffForExitAsync).Task.Unwrap();
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
        var activeSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (expectedSessionId is { } expected && activeSessionId is { } active && active != expected)
            throw new InvalidOperationException(Locale.Format("Svc.Status.SessionChanged", expected, active));

        // 注销不排在任务计划程序 COM 后面；先撤销所有连接/启动意图。
        _sessionClosing = true;
        InvalidateSessionOperations();
        try
        {
            _autoLaunchPending = false;
            _initialConnectionRetriesRemaining = 0;
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _rdpSettingsReconnectActive = false;
            _connectionAttemptCompletionSource?.TrySetResult(false);

            TryDisconnectRdpHost();
            RefreshState(Locale.Get("Svc.Status.LoggingOff"));

            _logoffRequest = (expectedSessionId ?? activeSessionId) is { } targetSessionId
                ? Task.Run(() =>
                {
                    using var requestTiming = new PerformanceTrace("Windows 注销请求下发");
                    return ChildSessionNativeMethods.TerminateChildSession(targetSessionId, wait: false);
                })
                : Task.FromResult<uint?>(null);
            var operation = new SessionLogoffOperation(_logoffRequest,
                ChildSessionNativeMethods.TryGetChildSessionId, TimeSpan.FromSeconds(15),
                _disposeCancellationTokenSource.Token);
            await operation.Completion;
            var terminatedSessionId = await operation.Request;
            _desktopWindow?.Hide();
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
        ChildSessionNativeMethods.DisableChildSessions();
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
            var enabled = ChildSessionNativeMethods.IsChildSessionsEnabled();
            ChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
            ConnectedState = _desktopWindow?.RdpHost.ConnectedState ?? 0;

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
            TopmostEnabled, SmartSizingEnabled, KeepAspectRatio, SendSystemShortcutsToRemote,
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
    private static string DescribeTermServiceForStatus()
    {
        var (state, startType) = ChildSessionEnvironmentCheck.QueryTermService();

        if (startType == "Disabled")
        {
            return Locale.Get("Svc.Status.TermDisabled");
        }

        return state is "Stopped" or "StopPending" ? Locale.Get("Svc.Status.TermStopped") : string.Empty;
    }

    /// <summary>
    /// 用户点「启动」时，把「已停止」的远程桌面服务拉起来。
    ///
    /// 只处理「启动类型正常、但服务没在运行」这一种情况：被禁用的服务不在这里动，
    /// 那需要改启动类型，属于用户的显式选择，交给诊断面板处理。
    /// 服务本来就在运行（或已禁用）时返回 false，调用方据此决定要不要重新预检。
    /// </summary>
    /// <param name="error">启动失败的原因；没做事或成功时为空。</param>
    /// <returns>本次是否真的把服务启动起来了。</returns>
    public bool TryStartTermServiceIfStopped(out string error)
    {
        error = string.Empty;

        var (state, startType) = ChildSessionEnvironmentCheck.QueryTermService();
        if (startType == "Disabled" || state is not ("Stopped" or "StopPending"))
        {
            return false;
        }

        if (!ServiceNativeMethods.TryStart("TermService", out var startError))
        {
            error = startError;
            AppLog.Warn($"启动远程桌面服务失败：{startError}");
            return false;
        }

        // 服务启动是异步的：等它真的进入 Running，调用方才能据此重新预检。
        const int timeoutSeconds = 5;
        for (var elapsed = 0; elapsed < timeoutSeconds * 5; elapsed++)
        {
            if (ServiceNativeMethods.TryQueryState("TermService", out var current) && current == 4)
            {
                AppLog.Info("远程桌面服务已自动启动。");
                return true;
            }

            Thread.Sleep(200);
        }

        error = Locale.Format("Svc.Error.TermServiceStartTimeout", timeoutSeconds);
        AppLog.Warn(error);
        return false;
    }

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
        var issues = await Task.Run(() => ChildSessionEnvironmentCheck.CollectForConnection()).ConfigureAwait(false);
        await PublishEnvironmentIssuesIfCurrent(issues, generation).ConfigureAwait(false);
    }

    /// <summary>
    /// 发布预检结果，但如果期间已经有更新的一轮开始了就丢弃这一份。
    ///
    /// 两处都会调它：启动流程的预检、界面/切换流程的单独预检。没有代际的话，
    /// 一轮慢扫描回来会把更新的结论盖掉，界面据此做出错误的阻塞判断。
    /// </summary>
    private Task PublishEnvironmentIssuesIfCurrent(
        System.Collections.Generic.IReadOnlyList<ChildSessionEnvironmentCheck.Issue> issues,
        int generation)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        void Publish()
        {
            if (_disposed) return;
            lock (_environmentGate)
            {
                // 只有比「已经发布过的」更旧才丢弃。写成一味要求等于当前代际的话，
                // 新一轮刚起跑时这一份会被扔掉，而调用方（启动流程）随后读到的
                // 还是上一次的结论，可能漏掉阻塞项。
                if (generation < _environmentPublishedGeneration)
                {
                    AppLog.Info("丢弃过期的环境预检结果（已发布过更新的一轮）。");
                    return;
                }

                _environmentPublishedGeneration = generation;
            }

            PublishEnvironmentIssues(issues);
        }

        if (_dispatcher.CheckAccess())
        {
            Publish();
            return Task.CompletedTask;
        }

        // 走队列而不是同步 Invoke：调用方多半在池线程上等结果，没必要把它卡住。
        // 但要给出一个「发布完成」的信号，否则调用方没法知道自己读到的是不是新结论。
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Publish();
            }
            finally
            {
                completion.TrySetResult(true);
            }
        }));

        return completion.Task;
    }

    /// <summary>
    /// 发布一次预检结果（字段 + 日志）。调用方必须已经在 UI 线程上。
    /// </summary>
    private void PublishEnvironmentIssues(
        System.Collections.Generic.IReadOnlyList<ChildSessionEnvironmentCheck.Issue> issues)
    {
        _environmentIssues = issues;
        _environmentCheckedTick = Environment.TickCount64;

        foreach (var issue in issues)
        {
            AppLog.Warn($"环境预检：{issue.Title}。{issue.Summary}");
        }
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
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _disposeCancellationTokenSource.Cancel();
        _autoLaunchPending = false;
        _connectionAttemptCompletionSource?.TrySetCanceled();
        _connectionAttemptCompletionSource = null;
        _initialConnectionRetriesRemaining = 0;
        _pendingRdpSettingChanges = RdpSettingChange.None;
        _rdpSettingsReconnectRetriesRemaining = 0;

        if (_desktopWindow is not null)
        {
            _desktopWindow.IsVisibleChanged -= OnDesktopWindowVisibilityChanged;
            _desktopWindow.RdpHost.ConnectionFailed -= OnRdpConnectionFailed;
            _desktopWindow.RdpHost.LoginCompleted -= OnRdpLoginCompleted;
            TryDisconnectRdpHost();
        }

        try
        {
            // 应用正在退出，注销失败不应阻止主程序关闭；但会话残留会占着第二个桌面，必须尝试。
            // 带上当前会话号：若期间会话已被重建，宁可残留也不要误注销别人的会话。
            if (!_exitRequested || _logoffRequest is not { IsCompletedSuccessfully: true })
            {
                var sessionId = ChildSessionNativeMethods.TryGetChildSessionId();
                _ = sessionId is null
                    ? ChildSessionNativeMethods.TerminateChildSession(wait: false)
                    : ChildSessionNativeMethods.TerminateChildSession(sessionId.Value, wait: false);
            }
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            AppLog.Warn("退出时注销分身会话失败。", exception);
        }

        if (_desktopWindow is not null)
        {
            _desktopWindow.AllowClose = true;
            _desktopWindow.Close();
            _desktopWindow = null;
        }

        // 在途 COM 启动可能仍会释放此信号量；不用 Dispose 制造退出竞态。
        _sessionCancellation.Cancel();
        _sessionCancellation.Dispose();
        _disposeCancellationTokenSource.Dispose();
    }

    private void ConnectCore(string operationMessage)
    {
        var existingSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        var window = EnsureDesktopWindow();

        // 用户主动隐藏过窗口就别弹出来：连接本身不需要窗口可见，
        // 切回分身时突然弹窗会打断他正在做的事。
        if (!_windowHiddenByUser)
        {
            ShowDesktopWindow(window);
        }

        if (window.RdpHost.ConnectedState == 0)
        {
            // 会话是新建的才需要自动拉起目标程序；接管已有会话时不能替用户再开一份。
            _autoLaunchPending = existingSessionId is null
                                 && _settings.Current.Launch.AutoLaunchOnConnect
                                 && !string.IsNullOrWhiteSpace(_settings.Current.Launch.TargetPath);
            // 重试与「连接后自动启动目标程序」无关：输错密码的用户往往根本没开那个开关，
            // 而重连一次正是唯一能让他再输一次密码的途径。
            _initialConnectionRetriesRemaining = ConnectionRetryCount;
            _suppressCredentialDialogFill = false;
            MarkAttemptStarted();
            window.RdpHost.ConnectToChildSession(DesktopSize);
        }
        else if (window.RdpHost.ConnectedState == 1 && existingSessionId is { } staleId
            && !ChildSessionNativeMethods.IsSessionActive(staleId))
        {
            window.RdpHost.ReconnectToChildSession(DesktopSize);
        }

        RefreshState(operationMessage);
    }

    private MainWindow EnsureDesktopWindow()
    {
        if (_desktopWindow is not null)
        {
            return _desktopWindow;
        }

        _desktopWindow = new MainWindow(this, _settings);
        _desktopWindow.IsVisibleChanged += OnDesktopWindowVisibilityChanged;
        _desktopWindow.RdpHost.ConnectionFailed += OnRdpConnectionFailed;
        _desktopWindow.RdpHost.LoginCompleted += OnRdpLoginCompleted;
        _desktopWindow.RdpHost.SetCredentialProvider(ProvideAutoLoginCredential);
        _desktopWindow.RdpHost.SetSmartSizing(_settings.Current.Session.SmartSizing);
        _desktopWindow.RdpHost.SetSendSystemShortcutsToRemote(SendSystemShortcutsToRemote);
        _desktopWindow.RdpHost.SetAudioMuted(AudioMuted);
        _desktopWindow.RdpHost.SetRelativeMouseMode(RelativeMouseModeEnabled);
        return _desktopWindow;
    }

    /// <summary>
    /// 给 RDP 客户端提供自动登录凭据。关闭开关或没存密码时返回 null，退回交互式输入。
    /// </summary>
    private (string UserName, string Domain, string Password)? ProvideAutoLoginCredential()
    {
        if (_autoLogin is null)
        {
            return null;
        }

        return _autoLogin.TryGetCredential(out var credential)
            ? (credential.UserName, credential.Domain, credential.Password)
            : null;
    }

    private static void ShowDesktopWindow(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// 是否正处在连接流程里（RDP 正在连接，或有一次连接尝试在途）。
    ///
    /// 不能只看 <see cref="ConnectedState"/>：Connect 是异步发起的，刚调用完的那一小段
    /// 时间里状态还没变成 2，只看它会误判成「不在连接中」而把刚启动的凭据框监听停掉。
    /// 设置变更后的自动重连也要算进来——那段时间状态同样是 0，但它确实还会再连一次。
    /// </summary>
    private bool IsConnectionAttemptInProgress()
    {
        return ConnectedState == 2
               || _connectionAttemptCompletionSource is { Task.IsCompleted: false }
               || _rdpSettingsReconnectActive;
    }

    private bool ExpireSettingsReconnect()
    {
        if (!_rdpSettingsReconnectActive || _rdpSettingsReconnectRetryInProgress
            || Environment.TickCount64 < _attemptDeadlineTick) return false;
        _rdpSettingsReconnectActive = false;
        _rdpSettingsReconnectRetriesRemaining = 0;
        _settingsReconnectGeneration++;
        _desktopWindow?.RdpHost.CancelPendingReconnect();
        // 待应用设置留给下一次显式连接，超时本身不继续消耗重试或认证次数。
        return true;
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_statusTickInProgress || _disposed)
        {
            return;
        }

        _statusTickInProgress = true;
        try
        {
            RefreshState();
            if (ExpireSettingsReconnect())
            {
                TryDisconnectRdpHost();
                CompleteConnectionFailure(CreateConnectionTimeoutFailure());
            }

            // 连接中且开了自动登录：开始盯着可能出现的凭据弹窗。
            // 放在状态轮询里而不是每个连接入口各写一遍，重连、重试、设置变更全覆盖。
            // 重试这一轮例外：它的意义就是让用户重新输一次，自动填会把同一份（很可能
            // 已经失效的）密码再提交一遍，白白多一次失败登录。
            if (ConnectedState == 2
                && _autoLogin is { IsEnabled: true }
                && !_suppressCredentialDialogFill)
            {
                _autoLogin.StartWatching();
            }
            else if (!IsConnectionAttemptInProgress())
            {
                // 连接已经不在进行中（既没在连、也没有在途的连接尝试）就别再盯着凭据框：
                // 监听会自己跑到 60 秒上限，这段时间里本机任何一个凭据框都有可能被误填。
                _autoLogin?.StopWatching();
            }
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            RefreshState(exception.GetBaseException().Message);
        }
        finally
        {
            _statusTickInProgress = false;
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

            // 目标程序可能读取同一个程序目录下的配置，启动前先把设置刷到磁盘。
            _settings.Flush();

            var fileName = Path.GetFileName(executablePath);
            RefreshState(isAutomatic
                ? Locale.Format("Svc.Status.ReadyAutoLaunching", fileName)
                : Locale.Format("Svc.Status.Launching", fileName));
            await ChildSessionProcessLauncher.LaunchAsync(
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
    private static uint GetRequiredChildSessionId()
    {
        var childSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (childSessionId is null)
        {
            throw new InvalidOperationException(Locale.Get("Svc.Error.NoSession"));
        }

        return childSessionId.Value;
    }

    private static void EnsureChildSessionsEnabled()
    {
        if (!ChildSessionNativeMethods.IsChildSessionsEnabled())
        {
            ChildSessionNativeMethods.EnableChildSessions();
        }
    }

    /// <summary>
    /// 判断「普通权限运行」是否真的会降权。
    ///
    /// 计划任务的 TASK_RUNLEVEL_LUA 只在 UAC 启用时才产生受限令牌；
    /// UAC 关闭（EnableLUA=0）或使用内置 Administrator 账户时，LUA 等同于最高权限。
    /// 读不到注册表时按「不降权」处理，宁可多弹一次确认也不要静默提权。
    /// </summary>
    private static bool IsLeastPrivilegeEnforced()
    {
        try
        {
            using var policiesKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            return policiesKey?.GetValue("EnableLUA") is int enableLua && enableLua != 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return false;
        }
    }

    private void OnDesktopWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshState();
    }

    private void OnRdpConnectionFailed(
        object? sender,
        ChildSessionConnectionFailedEventArgs e)
    {
        if (_disposed || _sessionClosing) return;
        _lastConnectionFailure = e;

        // 这一轮连接结束了，停止盯凭据弹窗；下一次连接会重新开始计数。
        _autoLogin?.StopWatching();

        if (_pendingRdpSettingChanges != RdpSettingChange.None)
        {
            if (_rdpSettingsReconnectRetryInProgress)
            {
                return;
            }

            if (_rdpSettingsReconnectRetriesRemaining > 0)
            {
                RetryRdpSettingsReconnectAsync(e);
                return;
            }

            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectActive = false;
        }

        if (_initialConnectionRetriesRemaining > 0
            && !_connectionRetryInProgress)
        {
            RetryInitialConnectionAsync(e);
            return;
        }

        CompleteConnectionFailure(e);
    }

    // RDP 报告连接成功后稍作等待，让新分身会话的桌面初始化完成。
    // 使用 OnLoginComplete 等待实际登录完成，避免依赖固定时长的延时。
    private async void OnRdpLoginCompleted(object? sender, EventArgs e)
    {
        if (_disposed || _sessionClosing) return;
        _lastConnectionFailure = null;
        _initialConnectionRetriesRemaining = 0;
        _connectionRetryInProgress = false;
        _credentialFailureSuspected = false;
        // 登录成功之后一切照旧：下一次连接该自动填还是自动填。
        _suppressCredentialDialogFill = false;
        ChildSessionEnvironmentCheck.ClearConnectionFailure();
        _connectionAttemptCompletionSource?.TrySetResult(true);

        // 登录已经完成，再盯着凭据弹窗只会误填下一个（比如用户手动打开的安全对话框）。
        _autoLogin?.StopWatching();

        // 输入窗口每次连接都会新建，观看模式需要重新施加到新窗口上。
        _desktopWindow?.RdpHost.SetInputBlocked(WatchModeEnabled);

        var completedSettingChanges = _pendingRdpSettingChanges;
        _rdpSettingsReconnectActive = false;
        if (completedSettingChanges != RdpSettingChange.None)
        {
            _pendingRdpSettingChanges = RdpSettingChange.None;
            _rdpSettingsReconnectRetriesRemaining = 0;
            _rdpSettingsReconnectRetryInProgress = false;
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

        if (RelativeMouseModeEnabled || completedSettingChanges.HasFlag(RdpSettingChange.RelativeMouse))
        {
            RelativeMouseModeConfigured?.Invoke(this, EventArgs.Empty);
        }

        if (!_autoLaunchPending)
        {
            return;
        }

        _autoLaunchPending = false;
        await Task.Yield();
        if (_disposed)
        {
            return;
        }

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
                    else if (!IsLeastPrivilegeEnforced())
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

    private async void RetryRdpSettingsReconnectAsync(
        ChildSessionConnectionFailedEventArgs firstFailure)
    {
        _rdpSettingsReconnectRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;

        // 记下代际：等待期间可能切到主桌面，那次重试就不该再发起了。
        var generation = _settingsReconnectGeneration;
        var startGeneration = StartGeneration;
        var retryNumber = RdpSettingsReconnectRetryCount
                          - _rdpSettingsReconnectRetriesRemaining
                          + 1;
        _rdpSettingsReconnectRetriesRemaining--;
        var retryDelay = TimeSpan.FromSeconds(1 << retryNumber);

        AppLog.Warn(
            $"RDP 设置切换后的自动重连失败，将在 {retryDelay.TotalSeconds:0} 秒后进行第 {retryNumber} 次重试。"
            + $"错误：{firstFailure.Message}，错误代码：{firstFailure.ErrorCode}，"
            + $"扩展错误代码：{firstFailure.ExtendedErrorCode}");
        RefreshState(
            Locale.Format(
                "Svc.Status.RdpReconnectRetrying",
                retryDelay.TotalSeconds,
                retryNumber,
                RdpSettingsReconnectRetryCount));

        try
        {
            await Task.Delay(retryDelay, SessionCancellationToken);
            if (_disposed
                || startGeneration != StartGeneration
                || generation != _settingsReconnectGeneration
                || _pendingRdpSettingChanges == RdpSettingChange.None)
            {
                return;
            }

            RefreshState(
                Locale.Format("Svc.Status.RdpReconnectRetryNow", retryNumber, RdpSettingsReconnectRetryCount));
            _rdpSettingsReconnectRetryInProgress = false;
            MarkAttemptStarted();
            EnsureDesktopWindow().RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                Locale.Format("Svc.Error.RdpReconnectFailed", actualException.Message),
                errorCode);
        }
        finally
        {
            if (generation == _settingsReconnectGeneration && startGeneration == StartGeneration)
                _rdpSettingsReconnectRetryInProgress = false;
        }

        if (retryFailure is not null && generation == _settingsReconnectGeneration
            && startGeneration == StartGeneration)
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    private async void RetryInitialConnectionAsync(ChildSessionConnectionFailedEventArgs firstFailure)
    {
        var generation = StartGeneration;
        var completion = _connectionAttemptCompletionSource;
        var cancellationToken = SessionCancellationToken;
        _connectionRetryInProgress = true;
        ChildSessionConnectionFailedEventArgs? retryFailure = null;

        // 这一轮不自动填弹窗：留空的凭据框等用户自己输。要在等待之前就置位，
        // 否则断开过程中状态还是「连接中」，轮询可能抢先把监听重新挂上。
        _suppressCredentialDialogFill = true;

        var retryNumber = ConnectionRetryCount - _initialConnectionRetriesRemaining + 1;
        _initialConnectionRetriesRemaining--;
        var retryDelay = ConnectionRetryDelay;

        AppLog.Warn(
            $"分身 RDP 连接失败，将在 {retryDelay.TotalSeconds:0} 秒后重试"
            + $"（第 {retryNumber} 次，共 {ConnectionRetryCount} 次）。"
            + $"错误：{firstFailure.Message}，错误代码：{firstFailure.ErrorCode}，"
            + $"扩展错误代码：{firstFailure.ExtendedErrorCode}");
        RefreshState(
            Locale.Format(
                "Svc.Status.InitialRetrying",
                retryDelay.TotalSeconds,
                retryNumber,
                ConnectionRetryCount));

        try
        {
            await Task.Delay(retryDelay, cancellationToken);
            if (_disposed
                || generation != StartGeneration
                || !ReferenceEquals(completion, _connectionAttemptCompletionSource)
                || _connectionAttemptCompletionSource is null
                || _connectionAttemptCompletionSource.Task.IsCompleted)
            {
                return;
            }

            RefreshState();
            if (_desktopWindow is null)
            {
                return;
            }

            RefreshState(
                Locale.Format("Svc.Status.InitialRetryNow", retryNumber, ConnectionRetryCount));
            _connectionRetryInProgress = false;
            // 新的一次尝试有自己的超时预算，否则总超时会把刚开始的重试掐掉。
            MarkAttemptStarted();
            // OnLogonError 触发后 ActiveX 可能仍处于连接状态，必须先断开再重连。
            // 否则这里直接退出会丢失已捕获的真实错误，最终只剩外层连接超时。
            _desktopWindow.RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsExpectedChildSessionException(exception))
        {
            var actualException = exception.GetBaseException();
            var errorCode = actualException is COMException comException
                ? comException.ErrorCode
                : 0;
            retryFailure = new ChildSessionConnectionFailedEventArgs(
                Locale.Format("Svc.Error.InitialRetryFailed", actualException.Message),
                errorCode);
        }
        finally
        {
            if (generation == StartGeneration
                && ReferenceEquals(completion, _connectionAttemptCompletionSource))
                _connectionRetryInProgress = false;
        }

        if (retryFailure is not null && generation == StartGeneration
            && ReferenceEquals(completion, _connectionAttemptCompletionSource))
        {
            OnRdpConnectionFailed(this, retryFailure);
        }
    }

    /// <summary>
    /// 连接失败的统一收尾：「重试耗尽」与「连接超时」两条路径都经由这里附加诊断信息，
    /// 再更新状态并通知订阅者。
    /// </summary>
    private void CompleteConnectionFailure(ChildSessionConnectionFailedEventArgs e)
    {
        // 统一在这里补充诊断，让「重试耗尽」和「连接超时」两条失败路径给出同样的信息。
        var diagnosed = new ChildSessionConnectionFailedEventArgs(
            $"{e.Message}{BuildFailureDiagnosis()}",
            e.ErrorCode,
            e.ExtendedErrorCode);
        _lastConnectionFailure = diagnosed;
        _autoLaunchPending = false;
        _initialConnectionRetriesRemaining = 0;
        _rdpSettingsReconnectActive = false;
        _rdpSettingsReconnectRetryInProgress = false;
        _credentialFailureSuspected = IsLikelyCredentialFailure(diagnosed);

        // 留给「复制环境信息」：环境全绿却连不上时，这一行通常才是关键。
        // 用 e.Message 而不是 diagnosed.Message：后者带上了整段环境诊断，不适合塞进一行。
        ChildSessionEnvironmentCheck.RecordConnectionFailure(
            e.ErrorCode,
            e.ExtendedErrorCode,
            e.Message);

        _connectionAttemptCompletionSource?.TrySetResult(false);
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
    private ChildSessionConnectionFailedEventArgs CreateConnectionTimeoutFailure()
    {
        var timeoutMessage =
            Locale.Format("Svc.Error.Timeout", ConnectionTimeout.TotalSeconds);
        var lastDiagnostic =
            _desktopWindow?.RdpHost.LastConnectionDiagnostic
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

        if (ChildSessionNativeMethods.TryGetChildSessionId() is null)
        {
            builder.AppendLine().AppendLine();
            builder.Append(
                Locale.Get("Svc.Diag.NoSession"));
        }

        if (_environmentIssues.Count > 0)
        {
            builder.AppendLine().AppendLine();
            builder.Append(ChildSessionEnvironmentCheck.BuildSummary(_environmentIssues));
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
            _desktopWindow?.RdpHost.DisconnectSession();
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
        MainWindow window,
        RdpSettingChange settingChange,
        string operationMessage)
    {
        if (_sessionClosing) return false;
        RefreshState();
        if (window.RdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            RefreshState(Locale.Format("Svc.Status.SettingAppliedNextConnect", operationMessage));
            return false;
        }

        var previousSettingChanges = _pendingRdpSettingChanges;
        var previouslyActive = _rdpSettingsReconnectActive;
        _pendingRdpSettingChanges |= settingChange;
        _rdpSettingsReconnectActive = true;
        _rdpSettingsReconnectRetriesRemaining = RdpSettingsReconnectRetryCount;
        MarkAttemptStarted();
        try
        {
            window.RdpHost.ReconnectToChildSession(DesktopSize);
        }
        catch
        {
            _rdpSettingsReconnectActive = previouslyActive;
            _pendingRdpSettingChanges = previousSettingChanges;
            if (_pendingRdpSettingChanges == RdpSettingChange.None)
            {
                _rdpSettingsReconnectRetriesRemaining = 0;
            }

            throw;
        }

        RefreshState(Locale.Format("Svc.Status.SettingAppliedReconnecting", operationMessage));
        return true;
    }

    [Flags]
    private enum RdpSettingChange
    {
        None = 0,
        SystemShortcuts = 1,
        Audio = 2,
        Resolution = 4,
        RelativeMouse = 8
    }
}
