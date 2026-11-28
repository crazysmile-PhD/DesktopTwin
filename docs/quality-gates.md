# CI 质量门禁与验证边界

`build` 作业检查 PR、main 推送和发行标签；本地构建同样启用分析规则。

## 自动阻断的检查

- `global.json` 固定 .NET SDK 10.0.404，禁止自动滚动；升级时一起更新 SDK、锁文件并验证发布。
- 所有 SDK 项目启用 .NET 10 recommended 分析器、可空性与警告即错误。独立 Framework 代理由固定 SDK 的 Csc 任务引用系统 Framework 程序集编译，使用警告等级 4、警告即错误与确定性输出；不将其等同于主项目完整的分析器检查。
- CA1502 的方法圈复杂度上限为 25，CA1505 的可维护性指数下限为 20。CA1506 的类型耦合上限为 95、方法为 35，配置集中在 `build/CodeMetricsConfig.txt`。指标不能证明设计良好。
- 三个项目提交 NuGet 锁文件，CI 以 locked mode 还原并审计直接和传递依赖。普通还原就纳入单文件分析器，避免发布时额外引入未锁定的 ILLink 依赖。
- Debug、Release 均编译与测试，原生冒烟项目参与编译；测试有挂起诊断超时，作业有总超时。
- `verify-companion-build.ps1` 验证无修改建置不改写代理，强制重建确实重新生成代理且 SHA-256 不变。不以耗时设跨机器门槛；记录的耗时仅供观察。
- 架构测试限制 Models 对 UI、存储、服务和原生操作的依赖；诊断历史不能依赖窗口或原生操作。所有服务均禁止依赖具体视窗，无豁免。会话端口与 DesktopSessionHost 不能依赖 WPF 或原生控件；会话编排、连接状态机和注销观察还禁止依赖 Windows 实现、注册表和具体自动登录服务。
- Release 测试必须实际执行、全部通过、零跳过；空或缺少 TRX、运行级清理失败、缺少覆盖率、缺少受保护类型均失败。异步方法生成的状态机也接受覆盖率门槛。
- 原生桌面、ActiveX 与可见进程夹具使用 `WindowsDesktopTestGroup` 定义的同一集合，隔离共享桌面和 WinForms 状态；保留原有断言与截止时间。纯会话状态、协议和设置测试仍按 xUnit 默认并行执行。[集合并行规则](https://xunit.net/docs/running-tests-in-parallel)
- `build/coverage-policy.json` 对核心组件分别要求 90–95% 行覆盖率、80–90% 分支覆盖率。SessionConnection、DesktopSessionHost、AccountCredentialStore 与 CredentialDialogWatch 要求 95% 行、90% 分支覆盖率。外部命令另以真实隔离进程验证输出、退出、超时和进程树清理，不宣称已覆盖所有操作系统错误分支。上传的 Cobertura 保留全应用结果，不把核心覆盖率当作全应用覆盖率。
- 保留 XAML 绑定、资源、常量唯一来源检查。图标改为真实枚举成员精确匹配，程序集不存在时失败。
- Actions 固定提交 SHA，Dependabot 每周检查 NuGet 和 Actions 更新。两种单文件包都接受锁定还原与分析器检查。

## 本次修正

| 问题 | 修改与回归证据 |
| --- | --- |
| 合法 JSON 中的 Launch、Window、密码表可能为 null | 提取无 IO 的 `AppSettingsNormalizer`；覆盖空区块、空集合、密码迁移、大小写冲突、重复迁移与未来版本 |
| 刷新把渲染异常当成采集失败 | 仅捕获采集异常；测试渲染错误传播及后续恢复 |
| 临时任务清理在 finally 抛异常 | 改在异常路径等待清理，保留原始和清理异常；清理完成前不授权启动 |
| ApplySnapshot 同时维护历史与渲染，复杂度 26 | 提取 `DiagnosticIssueHistory`；覆盖问题重现、未触发项和完整性证据缺失时撤销旧的已解决状态 |
| ViewModel 清理入口缺少资源契约 | 实现幂等 IDisposable，由窗口关闭调用 |
| 备份文件名依赖当前文化、取消所有权不明确 | 持久化标识与文件名使用 invariant culture，显示保留 current culture；明确启动任务和 sfc 输出读取的取消及清理所有权 |

生产规则没有批量禁用。WPF Application/Window 由框架关闭钩子清理资源，三处 CA1001 有局部理由；四处有界 Unicode 原生缓冲区保留 CA1838 局部标注；RDP COM 适配器保留 HRESULT 异常语义。测试仅豁免场景命名、就地预期数组与 RDP 替身故意注入的 COM 异常。

WORK001 登记无会话/稳定连接零周期查询、设置写失败不阻断启动、接收端原始启动错误、未授权通道空闲与授权租约失效、隐藏全屏凭据观察和 ViewModel 订阅释放，以及普通快照不启动系统验证的行为。输入仍使用真实嵌入接收端验证空闲与断线；超时测试只授权心跳，不发送实际键鼠事件。没有以全应用覆盖率或硬件耗时替代这些行为证据。

自动登录回归执行实际 ViewModel 启停与清除密码命令，验证等待中立即同步监听且不增加系统查询；另覆盖空闲、断开和受抑制重试阶段不能因开关变化重启监听。测试使用临时设置、合成密码与监听端口替身，不填写真实凭据框。

## 尚未解决的架构债务

MainWindowViewModel、MainWindow、环境检查和 AutoLoginService 仍承担较多职责。ChildSessionService 仍组合设置命令、状态文本和自动启动策略，但 Windows 操作、调度和时间均已显式注入，连接重试只由 SessionConnection 拥有。LIFE002 用正常构造的服务、平台/RDP 替身和虚拟时间验证重试、超时、设置合并、过期回调、注销与程序启动取消；不再拼装未初始化对象或反射私有字段。这些测试覆盖编排契约，真实 WTS/ActiveX 连接仍有单独验证边界。

设置对象图已改为 sealed record、init-only 属性与不可变集合；SettingsService.Update 在短锁内发布完整快照，写盘使用独立锁，避免普通修改等待磁盘 I/O。AccountCredentialStore 通过快照读取与事务更新访问凭据。CredentialDialogWatch 持有监听 Task，并串行交接取消后的工作；已进入 UI Automation/COM 的调用仍须协作式收尾。后续仍应抽出连接状态机与平台端口，并继续拆分身份查询和 UI Automation。每次拆分先覆盖状态转换与资源所有权。

## 已修复项目的发布阻断规则

`build/release-gates.json` 登记每个修复的必需测试。CI 不仅要求测试通过，还要求指定名称的测试确实存在且执行；删掉、跳过或改名而未更新契约均会阻断发布。`verify-release-gates.ps1` 在打包前验证 TRX；独立 `release-gate` 检查要求整套 build 成功，发行作业同时依赖 build 和 release-gate。

| 规则 | 必须保持的行为 |
| --- | --- |
| VIEW001 | 默认可读的自动分辨率迁移；窗口恢复时完整进入工作区、标题栏可拖动；固定画面等比与原生控件边界不退化 |
| CTRL001 | 本地测试接口默认关闭，限制命令和输入范围；拒绝非法请求后仍可处理后续请求，慢速读取不丢失回复 |
| INPUT001 | 原始移动不经指针加速，相邻移动合并但不重排按钮；租约撤销释放注入状态；真实接收端核对父进程/会话，延迟启动和断线均有界 |
| INPUT002 | 可重用传输缓冲仍保留输入顺序；分段状态须完整读取，错误会话与无效状态被拒绝；逾时、取消及流的所有权有界 |
| INPUT002 | 重用传输缓冲仍保持顺序；分段回覆完整读取，拒绝无效状态/会话；逾时不续用取消的通道，取消与底层管道的所有权独立 |
| ARCH001 | 所有服务不得引用具体视窗；会话端口和视窗所有者不得引用 UI 或 Interop |
| LIFE001 | 延迟创建且只持有一个视窗；配置失败关闭部分创建对象；关闭前解除订阅并保留清理错误 |
| CRED001 | 密码策略不依赖平台/UI；加密失败保留旧记录、账户操作隔离、更新不修改读者快照、真实 DPAPI 往返验证 |
| TEST001 | 整合测试只清理自己的程序，退出竞态得到处理；即使所有案例通过，运行级清理失败也阻断发布 |
| SET001 | 设置对象图不可变；一次修改原子发布；并发冲刷只写完整快照；写盘失败保留未持久化义务，显式冲刷报告错误，解除占用后退出仍保存最新快照 |
| PROC001 | 输出读取与退出共享命令截止时间；大量输出不死锁；超时终止仍运行的进程树；父进程退出后遗留的输出管道也不能无限等待 |
| WATCH001 | 排队前捕获 token，工作完成后才释放 CTS；新监听等待旧监听收尾；取消和异常均被观察，重试预算不被轮询重置 |
| SFC001 | 过期校验取消排队并在取得闸门后复核；旧请求不启动 SFC，异常仍释放闸门 |
| VIEW002 | 拒绝或确认超时后允许相同分辨率重试；待确认请求合并，确认后及连接重置按实际状态去重 |
| INPUT003 | 单个计时器负责被动发现，不由状态回复追加 UI 任务；输入窗口缺失时不查询远端且不重置限频；捕获保留短租约，后台撤权；真实接收端单次查询不恢复周期心跳 |
| INPUT004 | 固定队列回绕、合并、越界与溢出不改变顺序；固定帧保留校验和分段读取；值快照不被缓冲复用改写；稳态队列、编解码和原生准备无逐包托管分配 |
| INPUT005 | 唤醒合并、单次计时器复用与空闲停用；同步和挂起 IO 逐次消费、分段读取与统一截止时间；关闭显式唤醒等待；受控异步交换及通知无逐包托管分配 |
| INPUT006 | 接收端复用原生 IO 存储，取消后排空再释放；真实管道分段帧及稳态分配；持键时 EOF／非法包／关闭／读写超时均释放一次；续期越过旧期限后仍有效、撤权回空闲；真实 Framework 截断帧超时 |
| INPUT007 | 输入回复满足写入前的状态请求，删除重复心跳；队列空转非空才通知，清除旧通知后复核持久状态；保留写入期间新请求、授权撤销、到期丢弃、顺序及关闭唤醒 |

全应用初次行覆盖率约 26%，主要缺口为 WPF UI、外部 COM、真实 Child Session 和系统修复路径。CI 通过不能证明游戏输入、图形性能或注销行为正常。原生冒烟探针只参与编译，需在支持的 Windows 客户端另行运行。启动集成测试创建并清理自己带唯一标识的程序和临时任务，不连接或注销真实子会话。

## 本地复现

```powershell
dotnet restore DesktopTwin.sln --locked-mode
dotnet build DesktopTwin.sln -c Debug --no-restore
dotnet test DesktopTwin.sln -c Debug --no-build --no-restore --blame-hang-timeout 2m
dotnet build DesktopTwin.sln -c Release --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File build/verify-xaml.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File build/verify-invariants.ps1
dotnet test tests/DesktopTwin.Tests/DesktopTwin.Tests.csproj -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory artifacts/test-results --logger "trx;LogFileName=tests.trx"
pwsh -NoProfile -File build/test-quality-gates.ps1
pwsh -NoProfile -File build/verify-test-results.ps1 -ResultsDirectory artifacts/test-results
pwsh -NoProfile -File build/verify-release-gates.ps1 -ResultsDirectory artifacts/test-results
pwsh -NoProfile -File build/verify-companion-build.ps1
```

使用空或新的覆盖率输出目录；校验器拒绝混杂的不同报告。失败时查看 Actions 的 `quality-reports` 附件。升级依赖后先普通还原以更新锁文件，再执行上述检查与工作流中的两种发布命令。
