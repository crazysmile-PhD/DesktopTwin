# 架构与操作边界

本文对应当前源码行为。Windows 或第三方程序的行为以接口定义和运行证据确认，不从注释推导性能保证。

## 组件

| 组件 | 职责 |
| --- | --- |
| `MainWindow` / `MainWindowViewModel` | 显示、命令、用户提示和服务组装 |
| `ChildSessionService` | 子会话连接、注销、设置重连、状态发布 |
| `RdpActiveXHost` | MsRdpClient10 COM 配置、回调和控件层排队重连 |
| `ChildSessionProcessLauncher` / `SessionLaunchWorker` | 临时任务只引导独立启动代理进入指定会话；清理后授权启动并核对回执 |
| `ChildSessionEnvironmentCheck` / `EnvironmentFixer` | 诊断采集与显式系统修复 |
| `AsyncSnapshotRefresh<T>` | 合并界面的异步采集请求，丢弃过期/关闭后的结果 |

会话服务直接持有窗口；这不是严格单向依赖架构。它同时保留服务层和控件层重试，仍有继续收敛的空间。

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
