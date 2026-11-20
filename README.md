# DesktopTwin · 桌面分身

在窗口里连接 Windows 自带的子会话，让另一组桌面程序在同一台电脑上运行。

本仓库延续 [batvbs/DesktopTwin](https://github.com/batvbs/DesktopTwin) 的实现；桌面分身代码最初来自 [BetterGI](https://github.com/babalae/better-genshin-impact)。许可证为 GPL-3.0，第三方说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 工作方式

程序调用 Windows Child Session API，并用系统的 RDP ActiveX 控件显示子会话。它有另一套桌面、窗口和应用进程，仍使用本机 CPU、GPU、内存与当前账户可访问的文件。它不提供虚拟机或沙盒隔离，也不保证软件能双开。

连接前只等待必要预检，保留全部阻塞项；计划任务、启动项和系统文件完整性由“诊断”按需完整检查。退出时等待 Windows 接受注销请求后关闭主程序，Windows 仍可能继续清理分身；“关闭系统 Child Sessions 功能”仍等待注销完成。

微软将 Child Session 定义为绑定父会话的特殊本机回环远程桌面会话。系统同时只能有一个处于活动且已连接状态的子会话；父会话结束时子会话也会结束。详见 [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions)。本程序另有全局单实例限制。

## 使用

1. 在 Windows 上运行构建产物。程序清单要求管理员权限；首次连接前会说明启用系统 Child Sessions 功能。
2. 点击“启动并连接”。连接完成后，在分身窗口内运行程序，或从控制中心选择要启动的本地 `.exe`。
3. 需要自动启动目标程序时启用对应设置。程序只在新建会话并完成登录后自动启动目标，接管已有会话时不重复打开。

正常的 Child Session 连接会复用父会话身份，不能把“首次必定弹登录框”当成固定流程。若实际出现凭据提示，按提示使用账户凭据；不要从 `C:\Users` 文件夹名称推断登录名。详见 [排查手册](docs/troubleshooting.md)。

| 操作 | 实际效果 |
| --- | --- |
| 隐藏窗口 | 保留 RDP 连接与分身程序；不代表停止图形处理 |
| 观看模式 | 屏蔽从此窗口进入分身的键鼠输入，继续显示画面 |
| 关闭并确认 / 退出 | 注销分身，里面的程序会结束，请先保存工作 |
| 改声音、系统组合键或分辨率 | 通过现有设置重连流程应用 |

## 画面与性能

默认选择“控制中心 → 桌面分辨率 → 适应窗口（推荐）”：按窗口可用空间和本机 DPI 请求远端桌面尺寸，避免把完整的 1920×1080 桌面缩成小图。窗口停止调整 300 毫秒后请求更新，不重连；RDP 拒绝请求或两秒内未确认实际尺寸时提示，保留当前连接。实际支持情况仍取决于 Windows/RDP。

选择 1920×1080 等固定分辨率时，始终按实际远端画面的比例等比显示，剩余空间留黑边。“自适应缩放”控制固定分辨率是否显示完整桌面；关闭时采用 1:1 像素，空间不足可能需要滚动。“保持宽高比”开关已移除，旧字段会被忽略，保存时移除。

原生 RDP 控件由黑色 WinForms 容器管理原点和填充布局，避免调整窗口后残留控件偏移、灰色空区或画面裁切。远端桌面尺寸改变时，即使控件宽高没有改变，也会重新排布原生缩放画面。

首次升级到设置结构 v2 时启用适应窗口，取消旧版自动进入小窗的状态。主动选择的小窗宽度为 750 DIP，普通窗口目标宽度为 1280 DIP；启动恢复和尺寸切换时会缩入所在显示器的工作区。标题栏拖到上边界时保留可点击的区域；换屏、改变 DPI 或从隐藏恢复时重新检查位置。新版中手动选择的固定分辨率与小窗状态会保留。

本机连接不等于零开销。另一个游戏进程、子会话桌面、RDP 显示与其他串流软件都可能占用资源；隐藏窗口也不是注销。

连接前程序自动尝试共享帧缓冲和硬件解码两项优化。不支持的属性会被跳过，不增加用户开关。日志中的“属性接受”只表示请求被接受，不能证明实际协商启用，更不能推导出固定帧率、零延迟、无损显示或某个内存占用。接口定义见 [IMsRdpExtendedSettings.Property](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property)。

低帧率、操作等待与高占用要分别测量，步骤见 [性能排查](docs/performance.md)。当前没有发布覆盖各显卡、Windows 版本与游戏的性能基准。

## 升级说明

已移除 UU 视角切换、热键、视角锁定和串流修复。旧设置中的对应字段会被忽略，保存时移除；自动登录等其他设置保留。

主窗口显示后会在后台删除旧版热键代理的自启项，停止当前用户数据目录内的旧代理并删除其程序、请求和心跳文件。清理失败会记入日志，下次启动重试；不会卸载或操作 UU 软件及其服务。分身内启动目标程序仍使用一次性启动代理，不安装自启项或常驻监听。

## 环境与限制

目标运行环境为 Windows 10/11，依赖 Windows 的 RDP ActiveX、Child Session API 和任务计划程序。框架依赖产物需要 .NET 10 Desktop Runtime；自带运行时产物不需要另装它。具体 SKU、补丁、策略及驱动组合是否可用，需要实际验证；家庭版上的提示不是完整兼容性结论。

“控制中心 → 相对鼠标（实验性）”默认关闭。开启后在分身会话内启动随连接存活的输入接收端，本机捕获 Raw Input 位移，经当前用户命名管道送入分身；RDP 保持绝对坐标，供普通桌面和菜单操作使用。切换仍通过既有流程重连，未连接时保存到下次连接。

游戏隐藏光标、窗口位于前台且鼠标在画面内时自动捕获；显示菜单、按住 Alt、隐藏窗口、切到观看模式或失去焦点时释放。Ctrl+Alt 可主动释放，再点击画面恢复；按键和点击按原生消息记录，快速操作也不依赖定时轮询恰好采到。原始移动替代捕获期间的 RDP 鼠标移动，避免双重发送。授权撤销时释放自己注入的按键/按钮；接收端断线或心跳超时则释放并退出。不安装驱动或自启项。后端使用 `SendInput`，具体游戏的 Raw Input 兼容性仍需实测。排查与证据见 [鼠标转视角](docs/troubleshooting.md#鼠标转视角)。

诊断面板包含环境检查与显式修复。部分检查是程序的启发式判断，不等于 Windows 官方前置条件。修复可能改注册表、服务、计划任务或系统文件；具体范围见 [诊断与修复边界](docs/auto-repair-feasibility.md)。

## 从源码构建

在 Windows 上安装 `global.json` 指定的 .NET SDK。构建还使用系统 .NET Framework 的 C# 编译器生成嵌入式的一次性程序启动代理。

```powershell
git clone https://github.com/crazysmile-PhD/DesktopTwin.git
cd DesktopTwin
dotnet restore DesktopTwin.sln --locked-mode
dotnet build DesktopTwin.sln -c Release -warnaserror
dotnet test DesktopTwin.sln -c Release --no-build
```

框架依赖发布示例：

```powershell
dotnet publish src/DesktopTwin/DesktopTwin.csproj -c Release -r win-x64 --self-contained false -o publish
```

`publish\DesktopTwin.exe` 依赖同目录发布文件与目标机运行时。发布流程中的单文件/自带运行时选项见 [.github/workflows/build.yml](.github/workflows/build.yml)。源码 `main` 的修复不代表已存在对应版本的 Release 包。

自带运行时单文件关闭程序集压缩，优先减少启动时解压工作，代价是 exe 更大；本地打包与 CI 使用相同设置。具体启动收益需在同一机器测量。日志中的“性能计时”区分应用首帧、必要预检、连接及注销阶段，详见[性能排查](docs/performance.md)。

## 设置、日志与文档

设置位于 `%AppData%\DesktopTwin\settings.json`，日志位于 `%AppData%\DesktopTwin\logs\`。保存的自动登录密码用 Windows DPAPI 的当前用户作用域保护；启用此功能前无需打开 Windows 系统自动登录。

界面内置简体中文，可放置同目录、同基础文件名的 `.lng` 文件替换译文，详见 [本地化说明](Localization/README.md)。

- [排查手册](docs/troubleshooting.md)：连接、凭据、黑屏、升级与反馈信息。
- [性能排查](docs/performance.md)：区分帧率、等待与资源占用。
- [本地交互桥接](docs/local-control.md)：显式启用受限 API，在实际分身中验证缩放、焦点与游戏输入。
- [架构](docs/architecture.md)：线程、操作边界和等待预算。
- [参与开发](CONTRIBUTING.md)：构建、测试和人工验证范围。
- [质量门禁](docs/quality-gates.md)：静态分析、架构依赖、覆盖率及剩余债务。
- [变更记录](CHANGELOG.md)：历史版本；当前行为以本说明及代码为准。
