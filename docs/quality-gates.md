# CI 质量门禁与验证边界

`build` 作业检查 PR、main 推送和发行标签；本地构建同样启用分析规则。

## 自动阻断的检查

- `global.json` 固定 .NET SDK 10.0.404，禁止自动滚动；升级时一起更新 SDK、锁文件并验证发布。
- 所有项目启用 .NET 10 recommended 分析器、可空性与警告即错误。独立 Framework 代理使用 `/warn:4 /warnaserror+`，不宣称它接受了现代 Roslyn 分析。
- CA1502 的方法圈复杂度上限为 25，CA1505 的可维护性指数下限为 20。CA1506 的类型耦合上限为 95、方法为 35，配置集中在 `build/CodeMetricsConfig.txt`。指标不能证明设计良好。
- 三个项目提交 NuGet 锁文件，CI 以 locked mode 还原并审计直接和传递依赖。普通还原就纳入单文件分析器，避免发布时额外引入未锁定的 ILLink 依赖。
- Debug、Release 均编译与测试，原生冒烟项目参与编译；测试有挂起诊断超时，作业有总超时。
- 架构测试限制 Models 对 UI、存储、服务和原生操作的依赖；诊断历史不能依赖窗口或原生操作。所有服务均禁止依赖具体视窗，无豁免。会话端口与 DesktopSessionHost 不能依赖 WPF 或原生控件。
- Release 测试必须实际执行、全部通过、零跳过；空或缺少 TRX、运行级清理失败、缺少覆盖率、缺少受保护类型均失败。异步方法生成的状态机也接受覆盖率门槛。
- `build/coverage-policy.json` 对核心组件分别要求 90–95% 行覆盖率、80–90% 分支覆盖率。DesktopSessionHost 与 AccountCredentialStore 要求 95% 行、90% 分支覆盖率。上传的 Cobertura 保留全应用结果，不把核心覆盖率当作全应用覆盖率。
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

## 尚未解决的架构债务

MainWindowViewModel、MainWindow、ChildSessionService、环境检查和 AutoLoginService 仍承担较多职责。具体视窗依赖已被端口替换，但会话服务仍直接调用 WTS，使用 Dispatcher/真实时间，连接重试仍分布于服务和 ActiveX 层。

凭据存取策略已抽出为 AccountCredentialStore，并以新密文表替换旧表，避免修改在途读者正在枚举的字典。后续仍应抽出连接状态机与平台端口，并继续拆分身份查询和 UI Automation。每次拆分先覆盖状态转换与资源所有权。

## 已修复项目的发布阻断规则

`build/release-gates.json` 登记每个修复的必需测试。CI 不仅要求测试通过，还要求指定名称的测试确实存在且执行；删掉、跳过或改名而未更新契约均会阻断发布。`verify-release-gates.ps1` 在打包前验证 TRX；独立 `release-gate` 检查要求整套 build 成功，发行作业同时依赖 build 和 release-gate。

| 规则 | 必须保持的行为 |
| --- | --- |
| ARCH001 | 所有服务不得引用具体视窗；会话端口和视窗所有者不得引用 UI 或 Interop |
| LIFE001 | 延迟创建且只持有一个视窗；配置失败关闭部分创建对象；关闭前解除订阅并保留清理错误 |
| CRED001 | 密码策略不依赖平台/UI；加密失败保留旧记录、账户操作隔离、更新不修改读者快照、真实 DPAPI 往返验证 |
| TEST001 | 整合测试只清理自己的程序，退出竞态得到处理；即使所有案例通过，运行级清理失败也阻断发布 |

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
```

使用空或新的覆盖率输出目录；校验器拒绝混杂的不同报告。失败时查看 Actions 的 `quality-reports` 附件。升级依赖后先普通还原以更新锁文件，再执行上述检查与工作流中的两种发布命令。
