# 架构与操作边界

本文对应当前源码行为。Windows 或第三方程序的行为以接口定义和运行证据确认，不从注释推导性能保证。

## 组件

| 组件 | 职责 |
| --- | --- |
| `MainWindow` / `MainWindowViewModel` | 显示、命令、用户提示和服务组装 |
| `WpfDesktopSessionView` / `RdpSessionClient` | 将具体窗口与原生控件适配到 UI 无关的会话端口 |
| `DesktopSessionHost` | 延迟创建一个视窗，配置初值、转发事件并负责关闭前解除订阅 |
| `AccountCredentialStore` | 无平台依赖的账户密码策略，使用注入的保护函数、快照读取和原子更新；发布不可变密文表 |
| `TrayIconController` | 托盘菜单与具体窗口的呈现交互，属于 View 层 |
| `ChildSessionService` | 子会话连接、注销、设置重连、状态发布 |
| `RdpActiveXHost` | MsRdpClient10 COM 配置、回调和控件层排队重连 |
| `DesktopViewport` | 按桌面分辨率在可用区域内等比居中安排原生 RDP 宿主，窗口与全屏共用布局 |
| `ChildSessionProcessLauncher` / `SessionLaunchWorker` | 临时任务只引导独立启动代理进入指定会话；清理后授权启动并核对回执 |
| `ChildSessionEnvironmentCheck` / `EnvironmentFixer` | 诊断采集与显式系统修复 |
| `AsyncSnapshotRefresh<T>` | 合并界面的异步采集请求，丢弃过期/关闭后的结果 |
| `SettingsService` | 发布不可变设置快照；事务更新、合并保存与同步冲刷，磁盘锁不阻塞普通更新 |
| `CredentialDialogWatch` | 持有监听工作及取消来源；串行交接新旧监听并观察异常 |
| `BoundedProcessRunner` | 输出读取与退出共用截止时间；超时关闭输出并尽力终止仍运行的进程树 |
| `AppSettingsNormalizer` | 无 IO 的设置修复与版本迁移；SettingsService 负责存储和延迟写入，原有 JSON 字段名保持兼容 |
| `DiagnosticIssueHistory` | 独立维护诊断历史与已解决证据；DiagnosticsWindow 负责渲染 |

App 提供视窗工厂并保留具体 MainWindow 引用。会话服务只使用 `IDesktopSessionView`、`IRdpSessionClient`，不创建或引用具体窗口和 ActiveX 控件。视窗配置失败会关闭部分创建的视窗，重试时重新创建；关闭前先解除事件订阅。

架构测试禁止任何服务依赖具体窗口，没有豁免名单；契约和 DesktopSessionHost 也不能依赖 WPF 或 Interop。服务层仍使用 UI Dispatcher 调度和 WTS 平台接口，连接重试也仍分布在服务和控件两层。ViewModel 通过幂等 `Dispose` 释放资源；WPF 窗口在关闭时调用。发布门禁和后续拆分见 [质量门禁](quality-gates.md)。

## 画面布局

`DesktopViewport` 根据 `DesktopAspectRatio` 测量并安排 `WindowsFormsHost` 的实际矩形，不使用视觉变换缩放原生 HWND。窗口贴边、调整尺寸、隐藏恢复及分辨率变更均由 WPF 布局重新计算；多余空间露出父容器的黑色背景，不再改写系统分屏给出的外层窗口尺寸。比例固定跟随分身分辨率，旧版 `KeepAspectRatio` 设置不再参与运行。

`WindowsFormsHost` 安排 `RdpControlContainer`，由容器的 Dock 布局保持 ActiveX 的原点与填充尺寸，容器负责释放其子控件。直接承载 ActiveX 时，控件自行改变原生尺寸后，WPF 相同的布局约束可能不会再覆盖该尺寸；对应实现见 [.NET WPF 的 ArrangeOverride](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WindowsFormsIntegration/System/Windows/Integration/WindowsFormsHost.cs)。新回归测试覆盖纵向/横向比例往返和控件自行偏移；远端分辨率变化与登录完成也会排队刷新原生缩放，即使属性值或宿主矩形没变。

默认的 `AutomaticDesktopSize` 根据内容区物理像素和本机 DPI 计算 `RdpDisplaySettings`，初次连接即使用该尺寸；联机变化通过 300 毫秒合并的 `UpdateSessionDisplaySettings` 请求处理，不触发设置重连。日志区分请求接受和 `OnRemoteDesktopSizeChange` 确认；界面始终按回报的实际尺寸显示，拒绝或两秒内未确认时提示一次。不会因为尺寸更新失败自动断开或注销。接口依据为微软的 [UpdateSessionDisplaySettings](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/mt703457(v=vs.85)) 和 [缩放属性](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property)。

固定分辨率继续使用 `SmartSizing` 控制等比重采样或 1:1 显示。`WindowPlacement` 在物理像素坐标中处理工作区边界，允许其他显示器合法的负坐标。启动先恢复已有位置，再约束整个窗口；`WM_MOVING` 在拖动矩形生效前保留完整标题栏，显示器/DPI 改变与隐藏恢复也会检查位置。全屏由原有逻辑负责，普通窗口约束会跳过全屏。

设计参考 [Cockburn、Karlson、Bederson 的综述](https://www.csse.canterbury.ac.nz/andrew.cockburn/papers/fc.pdf) 对全局视图与细节导航的取舍，以及 [Windows 多显示器定位说明](https://learn.microsoft.com/en-us/windows/win32/gdi/positioning-objects-on-multiple-display-monitors)。适应窗口默认和合并等待是本项目的实现选择。

布局测试覆盖半屏、宽屏、自定义比例、负坐标恢复、缩入工作区、DPI 换算、拖动约束、隐藏恢复和未连接 ActiveX 的实际 HWND 边界；已连接 Child Session 的动态分辨率协商、渲染与游戏鼠标仍需单独验证。

## 连接

`StartAsync` 切回 UI Dispatcher，同一启动代际内共用未完成的启动任务。请求先校验代际，再决定是否共用。已有且实际活动的连接直接返回；已有会话的重连复用预检结果，新建会话前的近期预检快照可复用 30 秒。

`CollectForConnection` 与完整 `Collect` 共用全部阻塞检查及廉价提示，但连接路径不等待计划任务、启动项扫描或触发 sfc 完整性验证。完整诊断保留在诊断面板；未做完整扫描不表示环境已全面验证。残留临时任务的后台清扫推迟到主窗口首帧之后。

连接配置的目标固定为 `127.0.0.1`，并设置 `ConnectToChildSession`。本机图形优化由 `RdpLocalGraphicsOptions` 在连接前分别尝试；失败不会阻断必要设置。控件返回成功不等于图形路径已协商成功。

实验性相对鼠标由 `SessionSettings.RelativeMouseMode` 保存，默认关闭。`RdpRelativeMouseOptions` 先允许 `AllowRelativeMouseMode`，再请求 `AdvancedSettings7.RelativeMouseMode`；任何一步不受支持，就尽力将两项复位为 false，继续连接并报告未完整接受。关闭时也显式复位两项，避免复用控件时遗留上一连接的模式。反射包装的 COM 错误同样按可选属性处理，程序错误不静默吞掉。

鼠标设置通过 `ChildSessionService` 的既有设置重连入口合并，并沿用代际取消、注销与超时边界。登录完成后界面报告设置是否被接受；`OnMouseInputModeChanged` 仅记录控件报告的模式，不把属性接受或回调当成游戏兼容证明。原生控件负责鼠标输入与焦点，本功能不安装额外钩子、不自行锁定或隐藏本机光标、不经代理注入输入。[RelativeMouseMode](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings6-relativemousemode)、[扩展属性](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property)、[模式回调](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onmouseinputmodechanged)。

启动任务通过 `TaskCompletionSource` 等待登录完成回调，单次尝试预算 60 秒。首次连接错误最多自动重试一次，延迟 4 秒。超时本身不触发这一轮重试。设置重连保留最多三次服务层重试，延迟 2/4/8 秒；控件还会先等 1 秒及最多 10 秒会话释放。这些预算不是“整次操作只需几秒”的保证。

设置重连也为每次实际尝试记录 60 秒截止时间。UI 状态采样发现回调缺失且预算到期时，会作废控件排队重连并报告超时；退避等待期间不使用上一轮截止时间，下一次尝试重新计时。待应用设置保留给下次显式连接。

启动入口在已有连接时同时参考 ActiveX 连接状态和 WTS 会话活动状态。

## 取消与注销

`StartGeneration` 使旧启动、首次重试及设置重试无法写回新操作。注销/释放还取消捕获的会话生命周期 token。

注销先校验会话号、撤销旧意图，不等待程序启动占用的 COM 信号量。重复请求共用同一次注销。`SessionLogoffOperation` 区分请求接受与实际完成：WTS 注销使用 `wait: false`，普通注销/关闭系统功能随后异步观察最多 15 秒；窗口关闭及托盘退出只等待请求接受。退出期间仍阻止重新连接，释放资源时不重复下发已接受的注销。进程退出不证明 Windows 已完成清理；超时也只表示程序未确认完成。接口语义见 [WTSLogoffSession](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtslogoffsession)。

取消是协作式的。已经进入系统或外部 COM 的调用不一定能被 token 中止；代码不会把“调用方不再等待”当成“底层操作已经结束”。退出时仍尽力请求注销子会话。

## 后台工作与状态

游戏相对输入由 `RelativeInputController` 持有：前台、控制模式、画面内指针和远端隐藏光标共同决定捕获。捕获期间对子窗口原生输入进行替换，只发送 Raw Input 位移；普通坐标继续由 RDP 处理。Raw Input 的注册仅有一个进程内所有者，退出捕获时有条件恢复原注册；失焦释放自己的 ClipCursor，不覆盖其他应用取得的限制。

`RelativeInputSession` 持有一个当前用户、同提升级别的管道，核对接收端 PID、路径、用户和会话。接收端为嵌入的 Framework 小程序，通过既有临时任务/启动代理进入会话；先查验任务删除再创建接收端。无窗口创建回执只证明进程创建，随后必须完成接收端自己的握手。启动确认期间发送未授权心跳，避免首次确认延迟耗尽活动输入租约。主 UI 的授权 250ms 过期；接收端活动心跳 1500ms 超时即关闭并释放输入。队列有界，仅合并相邻移动，按钮和按键保持顺序，过载断开而不静默丢弃释放。

此通道不创建自启项、不安装驱动，也没有任意程序或脚本命令。`InputLease` 在撤销、断线与退出时释放已注入的键/按钮；菜单释放指针按钮而保留诊断端按住的 Alt。API 测试的位移和键可进入同一会话内通道；硬件捕获仍须额外实测。实现参考 [RDPRelativeInput](https://github.com/TKMAX777/RDPRelativeInput)、[Moonlight](https://github.com/moonlight-stream/moonlight-qt/tree/master/app/streaming/input) 与 [GamingAnywhere 5.1.5](https://chuang.zoolab.org/pubs/pdf/2013mmsys.pdf)，以 C# 独立实现；没有引入其二进制或驱动。

显式传入 `--control-pipe` 时，`App` 持有本地测试桥接并在关闭时取消它。`Diagnostics.LocalControlBridge` 只负责当前用户 ACL、请求边界与协议；`View.DesktopControlTarget` 在 Dispatcher 上调用既有命令、读取状态和核对前台/输入焦点，`Interop.DiagnosticInput` 提供有限的原生输入。普通启动没有监听器。协议与实际验证边界见[本地交互测试桥接](local-control.md)。

- 状态采样仍为 UI 上每 500ms 一次；只在发布快照变化时触发 `StateChanged`。
- 诊断在打开、激活、手动重检和后台完整性校验完成时采集；采集在后台，界面只渲染快照。没有每两秒完整重扫。
- 计划任务扫描保留 30 秒缓存；文件完整性校验在后台串行运行。读取失败和未验证结果不能作为正常证据。
- 目标程序启动的任务计划程序 COM 调用放到后台。启动排队受会话 token 取消，下发前复查会话身份。
- `PerformanceTrace` 使用单调时钟记录阶段耗时；App 创建至首帧不包含此前的单文件解压、运行时加载或 UAC 等待。自带运行时单文件不启用程序集压缩，避免每次加载时的解压工作；它仍可能释放本机库，不等于完全无解包。

启动代理源码位于 `src/SessionLaunchWorker/`，构建时用 Windows .NET Framework 编译器生成独立 `DesktopTwin.LaunchWorker.exe` 并嵌入主程序。代理只接受一次性 `--launch-worker` 请求，完成或断开后退出，没有热键、心跳、文件监视或常驻模式。

升级清理 `LegacyUuAgentCleanup` 在首帧后运行，删除旧热键代理自启项及固定名称的遗留文件。停止进程前核对完整路径，只处理当前用户数据目录中的 `DesktopTwin.Agent.exe` 或其 `.old` 文件，保留一次性启动代理和其他路径的同名进程。旧版任务前缀仍仅用于清扫历史任务。

程序启动采用如下顺序，不再延迟 45 秒清理：

1. 临时任务只启动独立的 `DesktopTwin.LaunchWorker.exe --launch-worker`。启动代理进入目标会话并连接本地命名管道，主端核对代理的进程路径、会话和用户身份。
2. 主端删除临时任务，再通过 `GetTask` 确认任务不存在。删除失败在本次操作内有限重试；读取异常不能作为“不存在”的证据。只有确认删除后，才向代理发送目标程序路径和参数。
3. 目标由会话内代理直接创建，使用 `Process.Start` 返回的实际进程身份。代理在同一桌面观察可见窗口并用有超时的 `WM_NULL` 确认窗口线程能响应；主端再核对 PID、启动时间、路径和会话。只有这些证据齐全，界面和日志才报告成功。
4. 确认最多观察 30 秒，但证据到达即完成，不固定等待。启动前退出、无可见窗口或窗口无响应都不能报告成功。单实例转交、启动器派生其他程序、托盘/控制台程序可能无法按此条件确认；提示会说明未确认，已经创建的目标不会被当作“没有启动”而自动重试。窗口可响应也不等于游戏资源或业务功能全部加载完成。
5. 注册调用即使抛异常，也仍承担清理义务，防止“Windows 已注册但响应丢失”。取消不取消清理；正常退出停止接收新启动，等待在途操作收尾并重试本次未完成的清理。清理失败会阻止目标启动并报错，不能伪报成功或静默留给下次启动。

为覆盖强制终止发生在注册与删除之间的窗口，任务定义还包含一个禁用的时间触发器（不执行目标），两分钟后到期，`DeleteExpiredTaskAfter=PT0S` 由 Windows 调度服务回收，与 DesktopTwin 是否再次启动无关。这是崩溃恢复措施，正常路径在目标启动前就已查验任务不存在；系统到期回收的执行时刻由 Windows 决定，不承诺恰好两分钟删除。Windows 停机、服务失效或权限被撤销时，应用无法保证当场完成外部资源删除。启动清扫只保留作旧版任务迁移，且仅匹配两种任务前缀加 GUID，避开本次在途任务。

设计依据与验证见 [启动确认与清理](session-launch.md)。
