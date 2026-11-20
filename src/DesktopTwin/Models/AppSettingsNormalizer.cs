using System;
using System.Collections.Generic;

namespace DesktopTwin.Models;

/// <summary>Repairs deserialized settings and migrates defaults without accessing storage or UI.</summary>
internal static class AppSettingsNormalizer
{
    /// <summary>Returns whether repaired or migrated values need to be persisted.</summary>
    internal static bool NormalizeAndMigrate(AppSettings settings, string defaultAccount)
    {
        var changed = settings.Session is null || settings.Launch is null
            || settings.Window is null || settings.Automation is null;
        settings.Session ??= new SessionSettings();
        settings.Launch ??= new LaunchSettings();
        settings.Window ??= new WindowSettings();
        settings.Automation ??= new AutomationSettings();
        if (settings.Launch.RecentTargets is null)
        {
            settings.Launch.RecentTargets = [];
            changed = true;
        }

        changed |= NormalizePasswords(settings.Automation, defaultAccount);
        if (settings.SettingsVersion >= AppSettings.CurrentVersion) return changed;

        // Version 1 changed the default from smart sizing to native resolution.
        if (settings.SettingsVersion < 1) settings.Session.SmartSizing = false;
        settings.SettingsVersion = AppSettings.CurrentVersion;
        return true;
    }

    private static bool NormalizePasswords(AutomationSettings automation, string defaultAccount)
    {
        var changed = automation.AutoLoginPasswords is null;
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (automation.AutoLoginPasswords is not null)
        {
            foreach (var pair in automation.AutoLoginPasswords)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                {
                    changed = true;
                    continue;
                }
                changed |= normalized.ContainsKey(pair.Key);
                normalized[pair.Key] = pair.Value;
            }
        }
        automation.AutoLoginPasswords = normalized;
        var legacy = automation.LegacyAutoLoginPassword;
        if (string.IsNullOrWhiteSpace(legacy)) return changed;

        var account = string.IsNullOrWhiteSpace(automation.AutoLoginUserName)
            ? defaultAccount : automation.AutoLoginUserName.Trim();
        normalized.TryAdd(account, legacy);
        automation.LegacyAutoLoginPassword = null;
        return true;
    }
}
