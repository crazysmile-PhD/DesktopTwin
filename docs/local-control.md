# 本地交互测试桥接

此接口供实际 Windows/RDP 交互排查使用：测试端可读取宿主与远端尺寸、输入焦点、光标裁限和相对鼠标模式证据，再发送有界输入。它不证明游戏兼容，也不绕过游戏或 Windows 的登录、授权窗口。

## 启用与范围

普通启动不创建监听器。先退出旧的 DesktopTwin，确认分身内没有需要保留的工作，再显式启动测试实例：

```powershell
$pipeName = 'DesktopTwin.Control.' + [guid]::NewGuid().ToString('N')
Start-Process ./DesktopTwin.exe -ArgumentList @('--control-pipe', $pipeName)
```

该实例依旧需要管理员权限。命名管道仅授权当前 Windows 用户 SID，并拒绝网络登录令牌；同一用户的普通权限测试端也能访问。此处刻意不用同时比较提升级别的 `PipeOptions.CurrentUserOnly`，所以它是显式开启的受限控制入口。退出程序即关闭监听器，不建立 HTTP 端口、自启项或常驻代理。不要把它用于共享或不可信客户端。

## 协议

连接 `\\.\pipe\<pipeName>`，每个连接执行一条命令。请求与回复均为四字节小端有符号长度，随后为 UTF-8 JSON。请求限制为 1–4096 字节。读完完整回复后，客户端须写回一个确认字节再关闭连接，避免 Windows 断开命名管道时丢弃尚未读取的回复。一次连接的读写和操作共用五秒期限。

回复为 `{"ok":true,"result":...}` 或 `{"ok":false,"error":"..."}`。连接与启动目标命令返回的 `accepted` 只表示已排队，已有流程独立完成并保留自己的超时与授权提示。已进入外部 COM 的调用不能由管道期限强行中止。若连接中断，操作可能已经执行；先重新截图或查询状态，不自动重试输入、连接或启动操作。截图使用已有窗口捕获接口，不由此协议导出。

| 请求 | 行为 |
| --- | --- |
| `{"command":"state"}` | 读取连接、窗口/原生画面边界、远端实际尺寸、焦点、光标和鼠标模式证据 |
| `{"command":"connect"}` | 调用既有启动连接流程；不会代替用户处理凭据提示 |
| `{"command":"focus"}` | 激活宿主并尝试聚焦 RDP 输入窗口，返回焦点结果 |
| `{"command":"probe_focus"}` | 短暂打开应用自己的测试窗口，返回失焦前、失焦中、恢复后的状态；不操控其他应用 |
| `{"command":"release_mouse","milliseconds":100}` | 在宿主发送固定 Ctrl+Alt 释放手势，最长 1000 毫秒；0 毫秒可测试快速手势 |
| `{"command":"launch_target"}` | 调用已配置目标的启动流程，保留原有授权确认 |
| `{"command":"resize","width":960,"height":700}` | 以 DIP 调整宿主，限制在当前显示器工作区；不允许在不可调整的全屏状态执行 |
| `{"command":"click","x":400,"y":300,"alt":true}` | 在原生 RDP 画面内的物理像素坐标左击，可短暂按住 Alt；先等坐标传递，再按住按钮 `milliseconds`（默认 100ms，0 可测试瞬时点击） |
| `{"command":"mouse","x":100,"y":0}` | 从已位于画面内的光标位置发送相对移动，每轴最多 500；结果仍受系统指针加速和 RDP 模式影响 |
| `{"command":"key","key":"W","milliseconds":200}` | 按住允许键，最长 1000 毫秒，结束或取消后释放 |

允许键只有 W/A/S/D、Escape、Enter、Tab、Alt、Space，另有独立的固定 Ctrl+Alt 释放手势。没有路径、脚本、任意组合键、系统设置、注销或退出命令。输入与失焦测试、目标启动要求 RDP 已登录、处于控制模式、宿主是前台窗口，且输入焦点得到确认。用户切换到其他窗口后应停止输入测试。失焦测试只激活自己的临时窗口并自动关闭、恢复主窗口。

`relativeAccepted` 只代表连接前属性写入；`relativeReadback` 是登录后回读；`relativeReported` 是 RDP 模式变化回调。任意一个值都不能单独证明游戏转视角可用。对照普通桌面点击、Alt 菜单点击与未按 Alt 的视角变化，记录实际截图与状态，才能判断输入在哪一层失败。

会话内输入通道启用后，原生 RDP 保持绝对模式，上述旧字段不代表新通道状态。查询 `sessionInput.ready/captured/rawMoves/status.injectedMoves`；`mouse` 在捕获时直接把相对位移送入同一输入队列，`key` 在通道就绪时由会话内接收端执行。自动测试位移不能替代物理鼠标的 WM_INPUT 捕获验证。Alt 点击先把 Alt 送入会话，最多等待 1200 毫秒确认远端光标可见且捕获已释放，再使用坐标点击；未确认时返回错误，避免把菜单点击变成游戏攻击。

接口依据：[命名管道选项](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions)、[断开时丢弃未读数据](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-disconnectnamedpipe)、[SendInput 与 UIPI](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)。
