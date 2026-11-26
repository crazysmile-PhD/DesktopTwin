using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Localization;
using DesktopTwin.Contracts;
using DesktopTwin.Services;
using Microsoft.Win32;

namespace DesktopTwin.Interop;

/// <summary>Adapts Windows session, diagnostics and process operations to the session lifecycle.</summary>
internal sealed class WindowsChildSessionPlatform(TimeProvider timeProvider) : IChildSessionPlatform
{
    /// <inheritdoc />
    public bool IsChildSessionsEnabled() => ChildSessionNativeMethods.IsChildSessionsEnabled();
    /// <inheritdoc />
    public uint? TryGetChildSessionId() => ChildSessionNativeMethods.TryGetChildSessionId();
    /// <inheritdoc />
    public bool IsSessionActive(uint sessionId) => ChildSessionNativeMethods.IsSessionActive(sessionId);
    /// <inheritdoc />
    public void EnableChildSessions() => ChildSessionNativeMethods.EnableChildSessions();
    /// <inheritdoc />
    public void DisableChildSessions() => ChildSessionNativeMethods.DisableChildSessions();
    /// <inheritdoc />
    public (string State, string StartType) QueryTermService() => ChildSessionEnvironmentCheck.QueryTermService();
    /// <inheritdoc />
    public Task<SessionEnvironment> CollectConnectionIssuesAsync() => Task.Run(() =>
    {
        var issues = ChildSessionEnvironmentCheck.CollectForConnection();
        return new SessionEnvironment(issues.Count > 0, ChildSessionEnvironmentCheck.HasBlockingIssue(issues),
            ChildSessionEnvironmentCheck.BuildSummary(issues));
    });
    /// <inheritdoc />
    public Task<uint?> RequestLogoffAsync(uint sessionId) => Task.Run(() =>
        ChildSessionNativeMethods.TerminateChildSession(sessionId, wait: false));
    /// <inheritdoc />
    public Task LaunchAsync(uint sessionId, string path, string arguments, bool elevated, CancellationToken cancellationToken) =>
        ChildSessionProcessLauncher.LaunchAsync(sessionId, path, arguments, elevated, cancellationToken);
    /// <inheritdoc />
    public void RecordConnectionFailure(int error, int? extendedError, string message) =>
        ChildSessionEnvironmentCheck.RecordConnectionFailure(error, extendedError, message);
    /// <inheritdoc />
    public void ClearConnectionFailure() => ChildSessionEnvironmentCheck.ClearConnectionFailure();

    /// <inheritdoc />
    public bool IsLeastPrivilegeEnforced()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            return key?.GetValue("EnableLUA") is int enableLua && enableLua != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> TryStartTermServiceIfStoppedAsync(CancellationToken cancellationToken) =>
        Task.Run(() => StartTermServiceCoreAsync(cancellationToken), cancellationToken);

    private async Task<bool> StartTermServiceCoreAsync(CancellationToken cancellationToken)
    {
        var (state, startType) = QueryTermService();
        if (startType == "Disabled" || state is not ("Stopped" or "StopPending")) return false;
        if (!ServiceNativeMethods.TryStart("TermService", out var error))
        {
            AppLog.Warn($"启动远程桌面服务失败：{error}");
            return false;
        }
        var startedAt = timeProvider.GetTimestamp();
        while (timeProvider.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ServiceNativeMethods.TryQueryState("TermService", out var current) && current == 4)
            {
                AppLog.Info("远程桌面服务已自动启动。");
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken).ConfigureAwait(false);
        }
        AppLog.Warn(Locale.Format("Svc.Error.TermServiceStartTimeout", 5));
        return false;
    }
}
