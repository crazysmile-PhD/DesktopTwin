# 启动确认与临时任务清理

目标是让“成功”附带可核对的运行证据，并把正常清理纳入同一次操作。固定等待不是目标就绪或任务已删除的证明。

## 协议

```text
主程序                     Windows 调度器               会话内启动代理
创建随机命名管道
注册并运行临时任务 ──────> 只启动启动代理 ──────────────> 连接管道并等待授权
核对连接者身份 <───────────────────────────────────────┘
删除任务并 GetTask 查验
确认不存在后发送目标路径 ──────────────────────────────> 直接创建目标，观察可响应窗口
核对目标 PID/创建时间/会话 <───────────────────────────── 成功或明确的未确认结果
```

临时任务的操作中不含目标程序路径及参数，只有引导代理的命令。主端在删除确认前崩溃，代理没有启动目标的授权；管道断开后退出。注册调用的响应丢失也会进入清理。清理重试与调用方取消分离，退出时停止接受新请求并等待清理。

任务的禁用触发器提供到期边界，Windows 自行到期删除覆盖主端无法执行 `finally` 的崩溃窗口。它不会成为另一次自动启动。到期时间为注册时刻后两分钟，不是正常路径的等待时间，也不把 Windows 的实际维护调度时刻当作精确保证。

## 证据与边界

进程创建时间使用两端共用的 `GetProcessTimes` 原始 FILETIME；映像路径用 `QueryFullProcessImageNameW` 读取。无窗口接收端可以在用户态加载器初始化前回报创建，不能把 `Process.MainModule` 此时尚未就绪当成路径不符。PID、会话、创建时间和完整路径仍须全部匹配；不匹配日志记录具体核验字段。内部输入接收端关闭失败管道后，仅对创建回执不匹配重试一次，每次重新握手；普通目标程序仍不自动重发。

| 情况 | 结果 |
| --- | --- |
| 任务运行请求返回 | 只说明引导开始，不能报告目标启动成功 |
| 临时任务删除返回成功 | 再查 `GetTask`；只有明确的不存在错误才接受，读取失败不算清理成功 |
| 目标窗口可见且回应有超时的 `WM_NULL` | 会话内代理回报，主端复核对应进程身份后显示窗口可响应 |
| 目标退出、无窗口、无响应、回执身份不符 | 未确认成功；给出原因，不自动重发目标启动 |
| 正常退出/注销取消 | 停止授权，清理仍执行；退出等待清理完成，失败明确报错 |
| 强杀主程序发生在任务注册后 | Windows 到期回收，不依赖再次打开 DesktopTwin |

这些检查确认进程及窗口层面的事实，不证明任意第三方程序的全部业务功能可用。启动器派生进程、单实例转交及托盘/控制台应用没有对应窗口证据时，返回未确认。系统服务永久不可用、断电或权限撤销也无法由应用凭空完成删除；不能对这些故障宣称无条件即时清理。

## 依据

- Saltzer、Reed、Clark，*End-to-End Arguments in System Design*，ACM TOCS 2(4)，1984，277–288，[作者原文](https://web.mit.edu/Saltzer/www/publications/endtoend/endtoend.pdf)。本文将其端到端确认原则用于启动：中间层接受请求不能替代目标端的运行证据。这是对设计原则的应用，不是论文对 Windows 行为的证明。
- [IRegisteredTask::RunEx](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex)：指定会话运行任务；返回成功与实际运行的区别。
- [schtasks delete](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-delete)：删除任务不会删除目标程序或中断已运行程序。旧注释声称“删除必然连带结束运行实例”，不能作为延迟 45 秒的通用接口依据。
- [TaskSettings.DeleteExpiredTaskAfter](https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-deleteexpiredtaskafter)：全部触发器超过结束边界后到期，由系统调度服务删除。
- [SendMessageTimeout](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)：窗口响应检查使用有界调用，避免被目标界面卡死拖住。
- [Process.WaitForInputIdle](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforinputidle)：适用于有消息循环的程序，不能独自证明可见窗口或业务就绪。
- [QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew) 与 [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)：核对 Windows 记录的映像路径和创建身份。

## 验证

`SessionLaunchTransactionTests` 注入注册响应丢失、连接失败、删除失败、取消与目标失败，检查所有清理义务以及“删除前不能授权”的顺序。

`SessionLaunchIntegrationTests` 使用真正的独立 Framework 启动代理、命名管道、Windows 进程及窗口，测试响应窗口、立即退出、隐藏进程、无响应窗口及断开。调度器测试还让目标在进程入口读取真实任务列表，验证目标开始前任务已经不存在。

`SessionTaskExpiryTests` 将相同的到期配置应用到短期任务，不运行主端清理、不再次启动应用，直接检查 Windows 是否自行移除任务。测试的应急清理只在断言之后执行，不能算作自动回收通过的证据。
