using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>升级时停用旧版 UU 热键代理；不触碰 UU 本身及一次性启动代理。</summary>
internal static class LegacyUuAgentCleanup
{
    internal static void Run()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTwin");
        try
        {
            using var startup = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            Remove(directory, startup);
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            AppLog.Warn("打开旧版热键代理自启项失败，继续清理代理文件。", error);
            Remove(directory, null);
        }
    }

    internal static void Remove(string directory, RegistryKey? startup)
    {
        try { startup?.DeleteValue("DesktopTwinSessionAgent", throwOnMissingValue: false); }
        catch (Exception error) when (IsExpectedFailure(error))
        { AppLog.Warn("删除旧版热键代理自启项失败。", error); }

        var executable = Path.GetFullPath(Path.Combine(directory, "DesktopTwin.Agent.exe"));
        // Only stop the retired executable at this user's exact data path. The launch
        // worker has a different name and path; other users' agents are left alone.
        foreach (var process in Process.GetProcessesByName("DesktopTwin.Agent"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(path, executable + ".old", StringComparison.OrdinalIgnoreCase))) continue;
                    process.Kill();
                    if (!process.WaitForExit(5000)) AppLog.Warn("旧版热键代理未在清理期限内退出。");
                }
                catch (Exception error) when (IsExpectedFailure(error))
                { AppLog.Warn("停止旧版热键代理失败。", error); }
            }
        }

        foreach (var name in new[] { "DesktopTwin.Agent.exe", "DesktopTwin.Agent.exe.old",
            "DesktopTwin.Agent.exe.new", "uu-switch-request.json", "session-agent-heartbeat.txt" })
        {
            try { File.Delete(Path.Combine(directory, name)); }
            catch (Exception error) when (IsExpectedFailure(error))
            { AppLog.Warn($"删除旧版热键代理文件失败：{name}", error); }
        }
    }

    private static bool IsExpectedFailure(Exception error) => error is IOException
        or UnauthorizedAccessException or SecurityException or InvalidOperationException
        or Win32Exception or NotSupportedException;
}
