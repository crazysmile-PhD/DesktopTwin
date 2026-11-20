# 参与开发

## 构建与检查

在 Windows 上安装 `global.json` 指定的 .NET SDK。项目使用 WPF、WinForms 和系统 RDP ActiveX；构建目标还会调用 Windows .NET Framework 的编译器生成独立代理。实际运行应用要求管理员权限，纯单元测试不因此获得操作真实会话的授权。

```powershell
dotnet restore DesktopTwin.sln --locked-mode
dotnet build DesktopTwin.sln -c Release -warnaserror
powershell -NoProfile -ExecutionPolicy Bypass -File build/verify-xaml.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File build/verify-invariants.ps1
dotnet test DesktopTwin.sln -c Release --no-build
```

四步分别检查编译、XAML 绑定/资源、常量单一来源和测试。工作流定义在 `.github/workflows/build.yml`；其他 fork 需要自行检查 Actions 是否启用，没有运行记录不能写成 CI 通过。

CI 另有 Debug 测试、recommended 静态分析、复杂度、架构依赖及核心覆盖率门禁。完整复现命令、规则例外与剩余架构债务见 [质量门禁](docs/quality-gates.md)。普通测试成功不代表覆盖率门禁也已通过。

在 Windows 11 24H2 或更新且支持相对鼠标的机器上，还可运行独立原生控件冒烟检查：

```powershell
dotnet run --project tests/DesktopTwin.NativeSmoke/DesktopTwin.NativeSmoke.csproj -c Release
```

该检查创建自己的未连接控件，调用生产代码反复打开/关闭鼠标属性并回读，不创建、连接或注销子会话。它独立于解决方案单元测试，不是跨 Windows 版本兼容性检查，也不能证明游戏输入正常。

## 合并与发布

`main` 要求通过 PR 合并、通过 `build` 检查、保持分支与目标分支同步并解决审查对话；禁止强制推送和删除，管理员也遵守规则。不要求额外审批人数，仓库维护者可合并通过检查的 PR。

发布前在 PR 中更新 `DesktopTwin.csproj` 的 `Version`、CHANGELOG 与 `build/release-notes/v<版本>.md`。合并并确认检查通过后，将对应的 `v<版本>` 标签推送到该合并提交。标签工作流会核对版本，构建、测试并生成两个 Windows x64 程序和包含各自 SHA-256 的 README；附件上传完成后公开 Release。不要重建或覆盖历史发行附件，历史迁移保留原文件并注明来源。

## 代码与文档

- C# 使用 UTF-8 无 BOM、可空引用类型；优先显式判空，不用 `!` 掩盖生产代码的问题。
- 私有字段 `_camelCase`，公共成员 `PascalCase`。新增类型和公共成员写 XML 注释。
- 关键注释解释操作所有权、取消与完成条件；不要把某次机器上的经验写成系统保证。
- XAML 只读状态显式使用 `Mode=OneWay`；翻译文字经 `Locale` / `loc:Loc`。
- 文件扫描及外部服务 COM 移到后台；RDP 控件操作仍须在宿主 UI 线程。超时返回不代表底层操作已停止。
- 新增开关前说明它解决的已证实需求，优先删除重复流程和自动回退；不要让用户替实现管理猜测中的兼容风险。

修改行为时同步更新 README、架构和排查说明。文档中的版本、耗时与性能数字必须来自当前代码或可复现测量，明确两者的区别。历史 CHANGELOG 和发行说明保留当时上下文。

## 验证范围

连接、注销、跨会话启动或图形配置的 PR 应分别列出自动测试和实际 Windows 验证，不能用属性替身测试证明真实渲染正常。

人工验证应覆盖：启动/设置重连期间注销，连续连接请求，窗口隐藏/退出，以及普通权限目标程序启动。注销会结束分身程序；执行前确认测试会话里没有需要保留的工作。

性能验证使用 [固定比较条件](docs/performance.md)，同时记录源码提交、真实进程和测量范围。没有测量时不宣称降低了多少 CPU、内存或延迟。

## 提交与反馈

一个 PR 说明一个可审查的问题、最终行为和验证结果。涉及 `Interop` 或任务计划程序时给出接口依据及尚未验证的边界。

反馈入口为 [crazysmile-PhD/DesktopTwin](https://github.com/crazysmile-PhD/DesktopTwin/issues)。附上复现步骤、时间、Windows 构建号、程序提交/版本及对应日志；不能先假定失败都来自用户系统。

项目采用 GPL-3.0。引入第三方代码时核对许可证并更新 `THIRD-PARTY-NOTICES.md`。
