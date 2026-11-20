using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using DesktopTwin.Interop;
using DesktopTwin.Localization;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 环境修复的第二组：处理 RDP 破解工具留下的残渣，以及 termsrv.dll 本体被改的情况。
///
/// 与主实现一致的三条约束：改前备份、改后回读、重启服务前检查有没有活动会话。
/// 额外两条针对本组的约束：
/// 4. 依赖服务只**追加**不覆盖（整键覆盖的还原脚本就是在这里把 Win11 的 StateRepository 冲掉的）；
/// 5. 安全参数只**补到默认**，绝不下调（已经加固到 3/4 的保持原样）。
/// </summary>
public static partial class EnvironmentFixer
{
    /// <summary>
    /// 把 ServiceDll 写回「原生路径 + REG_EXPAND_SZ」。
    ///
    /// 与 rdp-wrapper 的区别：那边是**路径被换成第三方 DLL**，这里是**值本身写坏了**
    /// （缺失，或类型被写成 REG_SZ）。后者的症状更隐蔽——服务起不来，
    /// 但 DLL 名字看起来完全正常。
    /// </summary>
    private static Result FixServiceDllType(CancellationToken cancellationToken)
    {
        var entry = ChildSessionEnvironmentCheck.GetServiceDllEntry();
        if (entry.Exists
            && entry.Kind == RegistryValueKind.ExpandString
            && !ChildSessionEnvironmentCheck.IsServiceDllHijacked(entry.Value))
        {
            return new Result(true, Locale.Get("Fix.DllType.AlreadyOk"));
        }

        var backupPath = WriteServiceDllState(entry);

        if (!TryWriteString(
                ChildSessionEnvironmentCheck.TermServiceParametersRegistryPath,
                "ServiceDll",
                ChildSessionEnvironmentCheck.GetNativeServiceDllPath(),
                RegistryValueKind.ExpandString,
                out var writeError))
        {
            return new Result(false, writeError);
        }

        var after = ChildSessionEnvironmentCheck.GetServiceDllEntry();
        if (!after.Exists || after.Kind != RegistryValueKind.ExpandString)
        {
            return new Result(false, Locale.Get("Fix.DllType.StillWrong"));
        }

        // 模块在服务启动时加载，改完必须重启服务才生效。
        var restart = RestartTermService(cancellationToken);
        if (!restart.Success)
        {
            return new Result(false, Locale.Format("Fix.DllType.NeedRestart", restart.Message));
        }

        var message = Locale.Get("Fix.DllType.Restored");
        if (backupPath is not null)
        {
            message += Locale.Format("Fix.BackupSaved", backupPath);
        }

        return new Result(true, message);
    }

    /// <summary>
    /// 恢复被整键覆盖的 TermService 服务键字段。
    /// </summary>
    private static Result FixServiceKey(CancellationToken cancellationToken)
    {
        var deviations = ChildSessionEnvironmentCheck.GetServiceKeyDeviations();
        if (deviations.Count == 0)
        {
            return new Result(true, Locale.Get("Fix.ServiceKey.AlreadyOk"));
        }

        var backupPath = WriteServiceKeySnapshot(deviations);
        var failures = new List<string>();

        foreach (var deviation in deviations)
        {
            var ok = deviation.Field switch
            {
                "ImagePath" => TryWriteString(
                    ChildSessionEnvironmentCheck.TermServiceKeyPath,
                    "ImagePath",
                    @"%SystemRoot%\System32\svchost.exe -k NetworkService",
                    RegistryValueKind.ExpandString,
                    out _),

                "ObjectName" => TryWriteString(
                    ChildSessionEnvironmentCheck.TermServiceKeyPath,
                    "ObjectName",
                    @"NT AUTHORITY\NetworkService",
                    RegistryValueKind.ExpandString,
                    out _),

                "DependOnService" => TryAppendServiceDependencies(out _),

                _ => true
            };

            if (!ok)
            {
                failures.Add(deviation.Field);
            }
        }

        if (failures.Count > 0)
        {
            return new Result(false, Locale.Format("Fix.ServiceKey.PartialFailed", string.Join(", ", failures)));
        }

        var restart = RestartTermService(cancellationToken);
        if (!restart.Success)
        {
            return new Result(false, Locale.Format("Fix.ServiceKey.NeedRestart", restart.Message));
        }

        var message = Locale.Get("Fix.ServiceKey.Restored");
        if (backupPath is not null)
        {
            message += Locale.Format("Fix.BackupSaved", backupPath);
        }

        return new Result(true, message);
    }

    /// <summary>
    /// 清理 RDP 破解工具的持久化项：计划任务导出 XML 后删除，自启项备份后删值。
    ///
    /// 先断持久化再改配置，否则这些任务会在下次开机把 DLL 写回去——
    /// 这是「修好又坏」的唯一成因。
    /// </summary>
    private static Result FixToolPersistence()
    {
        // 命中项和「扫没扫全」必须来自同一次扫描：分两次取的话，中间要是有人点了
        // 重新检测（会清缓存并把标记复位），这里就会拿一份「扫全了、没命中」的结论
        // 去报「没有需要清理的项」，而实际上漏掉的是读不到的目录。
        var hits = ChildSessionEnvironmentCheck.GetToolPersistenceHits(out var scanIncomplete);
        if (hits.Count == 0)
        {
            // 扫不完整时「没有命中」不等于「没有」：任务藏在读不到的目录里时，
            // 报「没有需要清理的项」会让用户以为已经干净了。
            return scanIncomplete
                ? new Result(false, Locale.Get("Fix.Persistence.Incomplete"))
                : new Result(true, Locale.Get("Fix.Persistence.Nothing"));
        }

        var backupDirectory = CreateToolBackupDirectory();
        var removedTasks = 0;
        var removedValues = 0;
        var failures = new List<string>();

        foreach (var hit in hits)
        {
            if (hit.Kind == "task")
            {
                if (RemoveScheduledTask(hit.TaskPath, backupDirectory))
                {
                    removedTasks++;
                }
                else
                {
                    failures.Add(hit.Target);
                }

                continue;
            }

            if (RemoveRunValue(hit, backupDirectory))
            {
                removedValues++;
            }
            else
            {
                failures.Add(hit.Target);
            }
        }

        ChildSessionEnvironmentCheck.InvalidateScanCache();

        if (failures.Count > 0)
        {
            return new Result(
                false,
                Locale.Format("Fix.Persistence.PartialFailed", string.Join("；", failures)));
        }

        return scanIncomplete
            ? new Result(
                true,
                Locale.Format(
                    "Fix.Persistence.RemovedIncomplete",
                    removedTasks,
                    removedValues,
                    backupDirectory))
            : new Result(
                true,
                Locale.Format("Fix.Persistence.Removed", removedTasks, removedValues, backupDirectory));
    }

    /// <summary>
    /// 把被降低的 RDP 安全参数补回系统默认值。只补不降。
    /// </summary>
    private static Result FixRdpSecurity()
    {
        var downgrades = ChildSessionEnvironmentCheck.GetRdpSecurityDowngrades();
        if (downgrades.Count == 0)
        {
            return new Result(true, Locale.Get("Fix.RdpSecurity.AlreadyOk"));
        }

        var failures = new List<string>();
        foreach (var downgrade in downgrades)
        {
            if (!TryWriteDword(
                    ChildSessionEnvironmentCheck.RdpTcpRegistryPath,
                    downgrade.Name,
                    downgrade.Expect,
                    out _))
            {
                failures.Add(downgrade.Name);
            }
        }

        ChildSessionEnvironmentCheck.InvalidateScanCache();

        if (failures.Count > 0)
        {
            return new Result(false, Locale.Format("Fix.RdpSecurity.PartialFailed", string.Join(", ", failures)));
        }

        // 这几项在远程桌面服务下次启动时生效；这里刻意不重启服务，
        // 免得为了一个安全参数的调整把用户正在用的分身打断。
        return new Result(true, Locale.Get("Fix.RdpSecurity.Restored"));
    }

    /// <summary>
    /// 修复被直接打过补丁（或丢失）的 termsrv.dll。
    ///
    /// 判定用 <c>sfc /verifyfile</c>（见 ChildSessionEnvironmentCheck.Termsrv.cs），
    /// 所以修复也用同一把尺子验收：**复制完必须复核通过才算成功**，
    /// 否则回滚原文件再交给 sfc —— 不能因为「看起来复制成功了」就宣布修好。
    ///
    /// 顺序：工具备份还原 → sfc /scanfile 就地修复 → sfc /scannow（独立窗口兜底）。
    /// 前两步都在 TermService 停止的窗口里跑并当场复验：文件没被映射，sfc 能就地替换，
    /// 修好了当场生效，不用重启。只有单文件都修不好时才退到完整扫描。
    /// </summary>
    private static Result FixTermsrvDll(CancellationToken cancellationToken)
    {
        Result result;
        try
        {
            result = FixTermsrvDllCore(cancellationToken);
        }
        catch (Exception original)
        {
            // 修复抛异常也要把服务拉回来，否则用户会剩一个停着的远程桌面服务。
            if (EnsureTermServiceRunning(out var recoveryError))
            {
                throw;
            }

            // 服务没能恢复：这条信息不能丢，否则调用方只看到最初的异常，
            // 完全不知道远程桌面此刻是不可用的。
            throw new InvalidOperationException(
                original.Message + Locale.Format("Fix.Termsrv.RestartFailed", recoveryError),
                original);
        }

        // 不管从哪条路径退出，服务都该在跑。
        //
        // 修复链路中途可能停过服务：停止超时后 TryStart 会失败（服务还在 STOP_PENDING，
        // 启动请求直接被拒），接着退到 sfc 兜底，那边没人再拉它。结果是文件修好了、
        // 远程桌面却连不上——所以恢复失败不能只写日志，必须反映到结论里。
        if (EnsureTermServiceRunning(out var restartError))
        {
            return result;
        }

        return new Result(
            false,
            result.Message + Locale.Format("Fix.Termsrv.RestartFailed", restartError));
    }

    private static Result FixTermsrvDllCore(CancellationToken cancellationToken)
    {
        // 走到这里就说明确认过文件被改：先把深扫义务记下来。
        // 修复会把文件还原成「完好」，之后若靠扫描去重新发现，就再也发现不了，
        // 而没删掉的开机任务会在下次开机把补丁写回去。
        ChildSessionEnvironmentCheck.MarkTamperingObserved();

        var state = ChildSessionEnvironmentCheck.GetTermsrvState();
        if (state.Exists && state.Verified && state.Intact)
        {
            return new Result(true, Locale.Get("Fix.Termsrv.AlreadyOk"));
        }

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var termsrvPath = Path.Combine(system32, "termsrv.dll");

        // 1) 用工具自己的备份还原，但必须复核通过。
        //    列表实时取：用户可能在上一轮检测之后才放入备份，用缓存里的旧列表会白白跳过它。
        string? stopFailure = null;
        foreach (var backupName in ChildSessionEnvironmentCheck.CollectBackupsNow())
        {
            var backupPath = Path.Combine(system32, backupName);
            if (!File.Exists(backupPath))
            {
                continue;
            }

            var restore = TryRestoreTermsrvFromBackup(
                termsrvPath, backupPath, cancellationToken, out var restoreStopError);
            stopFailure ??= restoreStopError;

            if (restore is not null)
            {
                return restore;
            }

            // 服务停不下来时，换下一个备份也是同样的结局：DLL 还被映射着，复制必然失败。
            // 跳出循环直接走 sfc，别把依赖服务来回停/启好几轮。
            if (restoreStopError is not null)
            {
                break;
            }
        }

        // 2) 用 sfc /scanfile 就地修：只校验这一个文件，几秒到几十秒。
        //    关键是它跑在服务停止的窗口里——文件没被映射，修复能当场落地，
        //    不像 /scannow 那样被占用、只能登记到重启后。
        cancellationToken.ThrowIfCancellationRequested();

        var scanfileRepair = TryRepairTermsrvWithSfcScanfile(
            termsrvPath, cancellationToken, out var scanfileStopError);
        if (scanfileRepair is not null)
        {
            return scanfileRepair;
        }

        // 3) 兜底：交回完整扫描，但不在本进程里等。
        //    进度和结论还给 sfc 自己的控制台，我们既不用解析它的输出，
        //    也不会因为超时去杀它——被杀会把替换登记成「重启后执行」，
        //    用户下次开机被塞进「更新并重启」，比多等几分钟糟得多。
        // 用户已经取消（或面板已关闭）就别再起一个几分钟的修复阶段。
        cancellationToken.ThrowIfCancellationRequested();

        var launch = LaunchSfcScannowInConsole(cancellationToken);

        var launchResult = launch switch
        {
            SfcLaunch.Launched => new Result(false, Locale.Get("Fix.Termsrv.ScannowLaunched")),
            SfcLaunch.AlreadyRunning => new Result(false, Locale.Get("Fix.Termsrv.ScannowAlreadyRunning")),
            _ => new Result(false, Locale.Get("Fix.Termsrv.ScannowLaunchFailed"))
        };

        if (stopFailure is not null || scanfileStopError is not null)
        {
            // 根因要跟着结论一起给用户看：只留在日志里，用户只会看到「sfc 跑过了还是不一致」。
            // 两步分开说——备份停不下服务、就地修复却停下来了，这种情况不能混成一句「都没进行」。
            launchResult = launchResult with
            {
                Message = (stopFailure is not null
                              ? Locale.Format("Fix.Termsrv.StopFailed", stopFailure)
                              : string.Empty)
                          + (scanfileStopError is not null
                              ? Locale.Format("Fix.Termsrv.ScanfileStopFailed", scanfileStopError)
                              : string.Empty)
                          + launchResult.Message
            };
        }

        return launchResult;
    }

    /// <summary>
    /// 用备份覆盖 termsrv.dll，覆盖后用 sfc 复核。复核不通过则回滚并返回 null，让调用方继续走 sfc。
    /// </summary>
    private static Result? TryRestoreTermsrvFromBackup(
        string termsrvPath,
        string backupPath,
        CancellationToken cancellationToken,
        out string? stopFailure)
    {
        stopFailure = null;

        if (HasActiveNonConsoleSession())
        {
            // 覆盖正在被服务使用的 DLL 要停服务；有活动会话时宁可不动。
            return new Result(false, Locale.Get("Fix.SessionsRunning"));
        }

        var safetyCopy = Path.Combine(
            GetDataDirectory(),
            "termsrv.dll.replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        try
        {
            if (File.Exists(termsrvPath))
            {
                File.Copy(termsrvPath, safetyCopy, overwrite: true);
            }

            if (!ServiceNativeMethods.TryStop("TermService", out var stopError))
            {
                // TryStop 是「等状态收敛」，超时也会返回 false，可服务此刻可能已经停了。
                // 再等一会儿：停稳了就照常用备份覆盖，不必白白退到耗时的 sfc。
                if (!WaitForServiceStopped("TermService", TimeSpan.FromSeconds(15)))
                {
                    // 真停不下来：尽量把服务拉回来，别让用户剩下一个停着的远程桌面服务；
                    // 同时把原因带出去——只写日志的话，用户最后只看得到「sfc 跑过了还是不一致」。
                    var restartNote = string.Empty;
                    if (!ServiceNativeMethods.TryStart("TermService", out var startError))
                    {
                        AppLog.Warn($"停止失败后恢复 TermService 失败：{startError}");
                        restartNote = Locale.Format("Fix.Termsrv.StopFailedNoRestart", startError);
                    }

                    AppLog.Warn($"停止 TermService 失败，跳过备份还原：{stopError}");
                    stopFailure = stopError + restartNote;
                    return null;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(backupPath, termsrvPath, overwrite: true);
            ServiceNativeMethods.TryStart("TermService", out _);

            var after = ChildSessionEnvironmentCheck.VerifyTermsrvNow();
            if (after.Exists && after.Verified && after.Intact)
            {
                return new Result(
                    true,
                    Locale.Format("Fix.Termsrv.RestoredFromBackup", Path.GetFileName(backupPath), safetyCopy));
            }

            // 这个备份不是原版：回滚，别把系统留在更糟的状态。
            if (File.Exists(safetyCopy))
            {
                ServiceNativeMethods.TryStop("TermService", out _);
                File.Copy(safetyCopy, termsrvPath, overwrite: true);
                ServiceNativeMethods.TryStart("TermService", out _);
            }

            AppLog.Warn($"备份 {Path.GetFileName(backupPath)} 未通过 sfc 校验，已回滚。");
            return null;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or SecurityException)
        {
            AppLog.Warn("从备份还原 termsrv.dll 失败。", exception);
            ServiceNativeMethods.TryStart("TermService", out _);
            return null;
        }
    }

    /// <summary>
    /// 等服务停下来。TryStop 是在等状态收敛，超时返回时服务可能还在停，
    /// 这时候直接 TryStart 必然失败，得先等它真正停下。
    /// </summary>
    // SERVICE_STATUS.dwCurrentState
    private const int ServiceStoppedState = 1;
    private const int ServiceStartPendingState = 2;
    private const int ServiceStopPendingState = 3;
    private const int ServiceRunningState = 4;

    /// <summary>
    /// 确保 TermService 处于运行状态。
    ///
    /// 停服务超时后 <see cref="ServiceNativeMethods.TryStart"/> 往往也失败：服务还在
    /// STOP_PENDING，启动请求会被直接拒绝。所以要先等它真正停下再启动；等不到就
    /// 如实报告「未恢复」，不能再发一次注定失败的启动请求。
    /// </summary>
    /// <param name="error">未能恢复到运行状态时的原因。</param>
    private static bool EnsureTermServiceRunning(out string error)
    {
        error = string.Empty;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!ServiceNativeMethods.TryQueryState("TermService", out var state))
            {
                error = Locale.Get("Fix.Termsrv.StateUnknown");
                return false;
            }

            if (state == ServiceRunningState)
            {
                return true;
            }

            if (state == ServiceStoppedState)
            {
                if (ServiceNativeMethods.TryStart("TermService", out var startError))
                {
                    return true;
                }

                AppLog.Warn($"修复后恢复 TermService 失败：{startError}");
                error = startError;
                return false;
            }

            // START_PENDING / STOP_PENDING：继续等，这一秒里发启动请求一定被拒。
            Thread.Sleep(200);
        }

        error = Locale.Get("Fix.Termsrv.StillPending");
        return false;
    }

    private static bool WaitForServiceStopped(string serviceName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ServiceNativeMethods.TryQueryState(serviceName, out var state) && state == ServiceStoppedState)
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return ServiceNativeMethods.TryQueryState(serviceName, out var last) && last == ServiceStoppedState;
    }

    /// <summary>
    /// 在 TermService 停止的状态下用 <c>sfc /scanfile=</c> 就地修复 termsrv.dll。
    ///
    /// 相比 /scannow 的两点好处：
    /// 1. 只校验一个文件，几秒到几十秒，用户不必等一次完整扫描；
    /// 2. 服务停着，文件没被映射，sfc 可以当场替换，不必等到重启才生效。
    /// </summary>
    /// <returns>修好返回结果；没修好返回 null，由调用方交给兜底。</returns>
    private static Result? TryRepairTermsrvWithSfcScanfile(
        string termsrvPath,
        CancellationToken cancellationToken,
        out string? stopFailure)
    {
        stopFailure = null;

        if (HasActiveNonConsoleSession())
        {
            return new Result(false, Locale.Get("Fix.SessionsRunning"));
        }

        var termServiceStopped = false;

        try
        {
            // 标志位提前置位：TryStop 是在等状态收敛，等待超时会返回 false，
            // 但服务此刻可能已经停了。跳过 TryStart 就等于把远程桌面关掉不还。
            termServiceStopped = true;
            if (!ServiceNativeMethods.TryStop("TermService", out var stopError)
                && !WaitForServiceStopped("TermService", TimeSpan.FromSeconds(15)))
            {
                AppLog.Warn($"停止 TermService 失败，跳过 sfc /scanfile 修复：{stopError}");
                stopFailure = stopError;
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var outcome = RunSfcScanfile(termsrvPath, cancellationToken);
            AppLog.Info($"sfc /scanfile 结果：{outcome.Description}");

            if (outcome.Deferred)
            {
                // 已有 sfc 在跑，或者它超时了还在跑。这时候既不能杀，也不能再起一个
                // /scannow 兜底——两个 sfc 同时改同一个文件，CBS 会互相打架。
                return new Result(
                    false,
                    Locale.Format("Fix.Termsrv.ScanfileDeferred", outcome.Description));
            }

            // 只认文件本身，不猜退出码：sfc 返回 0 既可能是「本来就一致」，
            // 也可能是「修好了」，还可能是「登记了重启后修复」，三者分不开。
            var after = ChildSessionEnvironmentCheck.VerifyTermsrvNow();
            if (after.Exists && after.Verified && after.Intact)
            {
                return new Result(
                    true,
                    Locale.Format("Fix.Termsrv.ScanfileRepaired", outcome.Description));
            }

            AppLog.Warn($"sfc /scanfile 未能修复 termsrv.dll（{outcome.Description}），转入完整扫描。");
            return null;
        }
        finally
        {
            // 修复没成功也要把服务拉回来：不能因为一次失败的修复让用户失去远程桌面。
            // sfc 用事务性替换，不会留下写到一半的文件，所以这里不需要像回滚备份那样
            // 担心重启服务会加载坏 DLL。
            if (termServiceStopped
                && !ServiceNativeMethods.TryStart("TermService", out var startError))
            {
                AppLog.Warn($"sfc /scanfile 结束后重启 TermService 失败：{startError}");
            }
        }
    }

    /// <summary>
    /// 单文件扫描的结果。
    /// </summary>
    /// <param name="ExitCode">sfc 退出码；没跑起来是 -1。</param>
    /// <param name="Description">给用户看的一句话。</param>
    /// <param name="Deferred">本次没有拿到结论：要么已有 sfc 在跑，要么它超时了还在跑。</param>
    private readonly record struct SfcOutcome(int ExitCode, string Description, bool Deferred = false);

    /// <summary>
    /// 运行 <c>sfc /scanfile=</c> 并等它结束。
    ///
    /// 两个「绝不」：
    /// 1. <b>绝不杀进程</b> —— sfc 的修复被中断只会变成 pending，下次开机强制跑一遍；
    /// 2. <b>绝不起第二个 sfc</b> —— 两个 sfc 同时改同一个文件，CBS 会互相打架。
    ///    所以已有 sfc 在跑、或本次超时仍未结束时，返回 Deferred 让调用方收手。
    /// </summary>
    private static SfcOutcome RunSfcScanfile(string path, CancellationToken cancellationToken)
    {
        var gate = ChildSessionEnvironmentCheck.SfcGate;

        // 和后台校验共用一把闸：后台 verifyfile 只要几秒，等一会儿比撞车划算。
        // 用带 token 的重载：用户取消时立刻退出，不必等闸、也不必白停一遍服务。
        if (!gate.Wait(TimeSpan.FromSeconds(30), cancellationToken))
        {
            return new SfcOutcome(-1, Locale.Get("Fix.Termsrv.ScanfileBusy"), Deferred: true);
        }

        var gateHandedOver = false;
        Process? process = null;
        try
        {
            // 闸只管本进程。用户自己开的 /scannow（或上一次留下的窗口）看不到闸，
            // 这时候再起一个 /scanfile 就是两个 sfc 抢同一个文件——先查进程。
            if (IsSfcRunning())
            {
                return new SfcOutcome(-1, Locale.Get("Fix.Termsrv.ScanfileBusy"), Deferred: true);
            }

            var sfc = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "sfc.exe");

            if (!File.Exists(sfc))
            {
                return new SfcOutcome(-1, Locale.Get("Fix.Termsrv.SfcMissing"));
            }

            // 不用 using：超时要把进程的所有权连同闸一起交给后台，
            // 这里提前 Dispose 会让后台的 WaitForExit 失去句柄。
            process = Process.Start(new ProcessStartInfo
            {
                FileName = sfc,
                Arguments = $"/scanfile={path}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,

                // sfc 输出是 UTF-16LE。不设这一行读到的是夹了一堆 \0 的乱码。
                StandardOutputEncoding = Encoding.Unicode
            });

            if (process is null)
            {
                return new SfcOutcome(-1, Locale.Get("Fix.Termsrv.SfcFailed"));
            }

            // 必须先挂上读再等退出：输出超过管道缓冲区时子进程会卡在写，双方一起等死。
            var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var started = process;

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (!started.WaitForExit(500))
            {
                if (DateTime.UtcNow > deadline)
                {
                    // 进程还活着，就让它跑完：这里既不能杀，也不能再起一个。
                    // 闸也不能就地放——放了等于允许下一个 sfc 进来跟还活着的这个抢同一个文件。
                    // 所有权交给后台，等它真正退出再放。
                    HandOverGateUntilExit(started, gate);
                    gateHandedOver = true;
                    process = null;
                    return new SfcOutcome(-1, Locale.Get("Fix.Termsrv.ScanfileTimeout"), Deferred: true);
                }
            }

            var text = outputTask.GetAwaiter().GetResult();
            return new SfcOutcome(started.ExitCode, DescribeSfcOutput(text, started.ExitCode));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or IOException)
        {
            AppLog.Warn("运行 sfc /scanfile 失败。", exception);
            return new SfcOutcome(-1, Locale.Get("Fix.Termsrv.SfcFailed"));
        }
        finally
        {
            process?.Dispose();
            if (!gateHandedOver)
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// 把闸的所有权交给 sfc 进程：等它真正退出后才释放。
    ///
    /// 超时时进程还活着，就地释放等于允许第二个 sfc 进来改同一个文件 —— CBS 会互相打架，
    /// 而我们要么拿不到结论、要么把文件改坏。挂在后台等待进程退出是唯一诚实的做法。
    /// </summary>
    private static void HandOverGateUntilExit(Process process, SemaphoreSlim gate)
    {
        _ = Task.Run(() =>
        {
            try
            {
                process.WaitForExit();
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                                  or System.ComponentModel.Win32Exception)
            {
                AppLog.Warn("等待超时的 sfc /scanfile 退出失败。", exception);
            }
            finally
            {
                process.Dispose();
                gate.Release();
            }
        });
    }

    /// <summary>
    /// 把 sfc 的中文输出压成一句能放进 UI 的话。
    /// 匹配不到就退回退出码——文案随系统语言变，不能拿它当判定依据，
    /// 判定一律看 <see cref="ChildSessionEnvironmentCheck.VerifyTermsrvNow"/> 的复验结果。
    /// </summary>
    private static string DescribeSfcOutput(string output, int exitCode)
    {
        if (output.Contains("未找到任何完整性违规", StringComparison.Ordinal))
        {
            return Locale.Get("Fix.Termsrv.ScanfileClean");
        }

        if (output.Contains("无法修复", StringComparison.Ordinal))
        {
            return Locale.Get("Fix.Termsrv.ScanfileCannotRepair");
        }

        if (output.Contains("成功修复", StringComparison.Ordinal))
        {
            return Locale.Get("Fix.Termsrv.SfcRepaired");
        }

        if (output.Contains("重新启动", StringComparison.Ordinal)
            || output.Contains("重启", StringComparison.Ordinal))
        {
            return Locale.Get("Fix.Termsrv.ScanfileNeedRestart");
        }

        return Locale.Format("Fix.Termsrv.ScanfileExitCode", exitCode);
    }

    /// <summary>
    /// 兜底扫描的启动结果。
    /// </summary>
    private enum SfcLaunch
    {
        Launched,
        AlreadyRunning,
        Failed
    }

    /// <summary>
    /// 弹出独立命令行窗口运行 <c>sfc /scannow</c>，把进度和结论交回给 sfc 自己。
    ///
    /// 刻意不等待、不重定向、不杀进程：
    /// 1. 它是保底手段，跑几分钟很正常，把主程序卡在那儿没有意义；
    /// 2. sfc 的修复无法真正中断——被杀掉只会把替换动作登记到下次开机，
    ///    用户会被塞进「更新并关机/重启」，再运行 sfc 还会被要求先重启。
    ///    与其冒险终止，不如让它自己跑完。
    ///
    /// 走到这里时 TermService 已经恢复运行，termsrv.dll 又处在被映射状态，
    /// 所以这次扫描多半也是登记 pending、重启后才替换——这是预期行为，
    /// 调用方会把「可能需要重启」写进给用户的话里。
    /// </summary>
    private static SfcLaunch LaunchSfcScannowInConsole(CancellationToken cancellationToken)
    {
        // 先确认本进程没有 sfc 在跑（后台 /verifyfile 可能正巧在复核）：
        // 直接查进程会把「自己的校验」误判成「别人在扫」，从而白白跳过兜底修复。
        var gate = ChildSessionEnvironmentCheck.SfcGate;

        bool gateHeld;
        try
        {
            // 用带 token 的重载：用户取消时不必在这里干等 30 秒再起一个几分钟的扫描。
            gateHeld = gate.Wait(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return SfcLaunch.Failed;
        }

        var handedOver = false;
        try
        {
            if (!gateHeld)
            {
                AppLog.Info("本进程的 sfc 校验仍在进行，跳过兜底扫描。");
                return SfcLaunch.AlreadyRunning;
            }

            // 持着闸还能看到 sfc 进程，那就是外部的（用户自己开的 /scannow 或上一次的窗口）。
            if (IsSfcRunning())
            {
                AppLog.Info("已有 sfc 进程在运行，跳过兜底扫描。");
                return SfcLaunch.AlreadyRunning;
            }

            var sfc = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "sfc.exe");

            if (!File.Exists(sfc))
            {
                AppLog.Warn("找不到 sfc.exe，无法启动兜底扫描。");
                return SfcLaunch.Failed;
            }

            // 外层 cmd 用 start 显式开一个新控制台，内层 /k 让窗口留着不关，
            // 用户能看到 sfc 的输出和最后结论。
            // 不用 CreateNoWindow=false 直接起 cmd：父进程要是本来就有控制台
            //（比如从终端启动的本程序），子进程会附到同一个窗口里，看起来像卡住了。
            //
            // 内层必须用绝对路径：cmd 对不带路径的命令是先查当前目录再查 PATH 的，
            // 而这个进程从可写目录启动时，那里放一个同名 sfc.exe 就会被以管理员权限执行
            //（CWE-426）。WorkingDirectory 一起钉到 System32 兜底。
            //
            // 两个 cmd 都加 /d：否则它们会执行 HKCU 下 Command Processor\AutoRun 里的命令，
            // 而同用户权限的进程可以写那个键——提权执行的正是别人的命令（CWE-78）。
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /c start \"\" cmd.exe /d /k \"{sfc}\" /scannow",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory
            });

            if (process is null)
            {
                return SfcLaunch.Failed;
            }

            // 闸要跟着这次完整扫描走到结束再放：外层 cmd 立刻退出，真正的 /scannow
            // 在另一个窗口里跑几分钟，中途放开会让后台 /verifyfile 撞进来。
            HandOverGateUntilScanCompletes(gate);
            handedOver = true;

            AppLog.Info("已在独立命令行窗口启动 sfc /scannow。");
            return SfcLaunch.Launched;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or IOException)
        {
            AppLog.Warn("启动 sfc /scannow 窗口失败。", exception);
            return SfcLaunch.Failed;
        }
        finally
        {
            if (gateHeld && !handedOver)
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// 把闸交给后台，等这次完整扫描真正结束再释放。
    ///
    /// 外层 <c>cmd /c start</c> 启动后立刻退出，扫描本体在另一个进程里跑几分钟，
    /// 而且从「cmd 起来」到「sfc.exe 出现在进程表」之间还有一小段间隔。
    /// 期间放开闸，后台 <c>/verifyfile</c> 就会闯进来和扫描抢同一个文件：
    /// 两个 sfc 争 CBS 的资源，校验结论也不可信。
    /// </summary>
    private static void HandOverGateUntilScanCompletes(SemaphoreSlim gate)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // 先等扫描进程真正出现，最多 10 秒（sfc 秒退或没起来就没什么可等的）。
                var appearDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (DateTime.UtcNow < appearDeadline && !IsSfcRunning())
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }

                // 再等它退出。给个上限，避免异常情况下把闸永久占住。
                var exitDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
                while (DateTime.UtcNow < exitDeadline && IsSfcRunning())
                {
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <summary>
    /// 是否已经有 sfc 在跑（含用户自己开的、上次窗口留下的）。
    ///
    /// 按进程名查而不是查我们的闸：闸只管本进程。实现放在检查侧，
    /// 校验路径也要用它，避免两处各写一份判断。
    /// </summary>
    private static bool IsSfcRunning() => ChildSessionEnvironmentCheck.IsSfcProcessRunning();

    // ---------------- 辅助 ----------------

    /// <summary>
    /// 追加缺失的服务依赖（RPCSS / StateRepository），保留本机已有的其它依赖。
    /// </summary>
    private static bool TryAppendServiceDependencies(out string error)
    {
        error = string.Empty;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ChildSessionEnvironmentCheck.TermServiceKeyPath, writable: true);
            if (key is null)
            {
                error = Locale.Format("Fix.RegistryOpenFailed", ChildSessionEnvironmentCheck.TermServiceKeyPath);
                return false;
            }

            var current = key.GetValue("DependOnService") as string[] ?? Array.Empty<string>();
            var wanted = new List<string>(current);

            void Ensure(string name)
            {
                foreach (var existing in wanted)
                {
                    if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                wanted.Add(name);
            }

            Ensure("RPCSS");

            using (var stateRepository = baseKey.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Services\StateRepository"))
            {
                if (stateRepository is not null)
                {
                    Ensure("StateRepository");
                }
            }

            if (wanted.Count == current.Length)
            {
                return true;
            }

            key.SetValue("DependOnService", wanted.ToArray(), RegistryValueKind.MultiString);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or SecurityException
                                              or IOException)
        {
            error = Locale.Format("Fix.RegistryWriteFailed", exception.Message);
            return false;
        }
    }

    /// <summary>
    /// 把当前 ServiceDll 的状态（含类型）写进备份文件。
    /// </summary>
    private static string? WriteServiceDllState(ChildSessionEnvironmentCheck.ServiceDllEntry entry)
    {
        try
        {
            var directory = GetDataDirectory();
            var path = Path.Combine(directory, "service-dll-backup.txt");
            File.WriteAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                (entry.Exists
                    ? $"Value={entry.Value}{Environment.NewLine}Kind={entry.Kind}{Environment.NewLine}"
                    : "<值不存在>" + Environment.NewLine));
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("备份 ServiceDll 状态失败。", exception);
            return null;
        }
    }

    /// <summary>
    /// 把服务键当前值导出成文本，便于手工回滚。
    /// </summary>
    private static string? WriteServiceKeySnapshot(IReadOnlyList<ChildSessionEnvironmentCheck.ServiceKeyDeviation> deviations)
    {
        try
        {
            var directory = GetDataDirectory();
            var path = Path.Combine(directory, "termservice-key-backup.txt");
            var builder = new StringBuilder();
            builder.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var deviation in deviations)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"{deviation.Field}: {deviation.Current}  ->  {deviation.Expect}");
            }

            File.WriteAllText(path, builder.ToString());
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("备份 TermService 服务键失败。", exception);
            return null;
        }
    }

    private static string GetDataDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateToolBackupDirectory()
    {
        var directory = Path.Combine(
            GetDataDirectory(),
            "rdp-tool-backup",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// 导出一份任务 XML 再删除任务。
    /// </summary>
    private static bool RemoveScheduledTask(string taskPath, string backupDirectory)
    {
        if (string.IsNullOrWhiteSpace(taskPath))
        {
            return false;
        }

        var schtasks = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "schtasks.exe");

        if (!File.Exists(schtasks))
        {
            return false;
        }

        try
        {
            var query = RunProcess(schtasks, $"/Query /TN \"{taskPath}\" /XML");
            if (query.Output is not null)
            {
                // 备份要能直接喂回 schtasks /Create /XML：它输出的 XML 头写的是 UTF-16，
                // 字节却是本地代码页，按 UTF-8 存回去中文任务名/描述会与声明对不上。
                // 文件名同样要截断：任务可能藏在很深的子树里，拼出来的路径会超长。
                var fileName = MakeSafeFileName(taskPath.Trim('\\')) + ".xml";
                File.WriteAllText(
                    Path.Combine(backupDirectory, fileName),
                    query.Output,
                    Encoding.Unicode);
            }

            // 删除必须看退出码：不看的话删失败也报「已清理」，面板刷新后问题还在，
            // 用户会以为修复本身是坏的。备份失败不拦删除（XML 已经尽力导出了）。
            var delete = RunProcess(schtasks, $"/Delete /TN \"{taskPath}\" /F");
            return delete.ExitCode == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            AppLog.Warn($"删除计划任务 {taskPath} 失败。", exception);
            return false;
        }
    }

    /// <summary>
    /// 备份并删除一处自启项。
    /// </summary>
    private static bool RemoveRunValue(
        ChildSessionEnvironmentCheck.ToolPersistenceHit hit,
        string backupDirectory)
    {
        try
        {
            var root = hit.RegistryRoot switch
            {
                "HKCU" => Registry.CurrentUser,
                "HKU" => Registry.Users,
                _ => Registry.LocalMachine
            };

            using var key = root.OpenSubKey(hit.RegistryPath, writable: true);
            if (key is null)
            {
                return false;
            }

            var data = key.GetValue(hit.ValueName) as string ?? string.Empty;

            // 原文留一份，万一删错了还能手工加回去。
            var fileName = MakeSafeFileName(hit.Target) + ".txt";
            File.WriteAllText(
                Path.Combine(backupDirectory, fileName),
                hit.Target + Environment.NewLine + data + Environment.NewLine,
                Encoding.UTF8);

            key.DeleteValue(hit.ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn($"删除自启项 {hit.Target} 失败。", exception);
            return false;
        }
    }

    /// <summary>备份文件名的正文部分最大长度，给目录路径留余量。</summary>
    private const int MaxBackupNameLength = 80;

    /// <summary>
    /// 把带非法字符的显示文本转成安全文件名。
    ///
    /// 长度要限制：自启项的显示文本是「注册表位置 → 值名」，值名可以很长，
    /// 拼出来的文件名一旦超过文件系统上限，备份就写不进去——而「备份失败就不删」
    /// 意味着这条永远修不好。
    ///
    /// 哈希必须按<b>原文</b>算，并且无条件拼上去：非法字符先被替换成 <c>_</c>，
    /// 只对替换后的文本求哈希时 <c>rdpwrap:a</c> 和 <c>rdpwrap?a</c> 会得到同一个名字，
    /// 第二个备份直接覆盖第一个，而两项都会被删掉——等于丢了一份备份。
    /// </summary>
    private static string MakeSafeFileName(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }

        var suffix = "_" + StableSuffix(text);
        var limit = MaxBackupNameLength - suffix.Length;
        var safe = builder.ToString();

        return string.Concat(safe.AsSpan(0, Math.Min(safe.Length, limit)), suffix);
    }

    /// <summary>
    /// 确定性的短哈希（FNV-1a）。用 <c>string.GetHashCode</c> 不行：那个每个进程都不一样，
    /// 重启一次就会在备份目录里多出一份同名文件。
    /// </summary>
    private static string StableSuffix(string text)
    {
        unchecked
        {
            // 64 位 FNV-1a：32 位时两个长前缀相同的目标只要哈希撞上就会互相覆盖，
            // 而两项都会被删掉——等于丢了一份备份。
            var hash = 14695981039346656037ul;
            foreach (var ch in text)
            {
                hash = (hash ^ ch) * 1099511628211ul;
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 跑一个外部命令，返回（标准输出，退出码）。跑不起来或超时返回退出码 -1。
    /// </summary>
    private static (string? Output, int ExitCode) RunProcess(string fileName, string arguments) =>
        BoundedProcessRunner.RunAsync(fileName, arguments, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
}
