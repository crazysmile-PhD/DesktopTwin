using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace DesktopTwin.Models;

/// <summary>Repairs deserialized settings and migrates defaults without accessing storage or UI.</summary>
internal static class AppSettingsNormalizer
{
    /// <summary>Returns whether repaired or migrated values need to be persisted.</summary>
    internal static bool NormalizeAndMigrate(ref AppSettings settings, string defaultAccount)
    {
        var changed = settings.Session is null || settings.Launch is null
            || settings.Window is null || settings.Automation is null;
        var launch = settings.Launch ?? new LaunchSettings();
        if (launch.RecentTargets is null)
        {
            launch = launch with { RecentTargets = [] };
            changed = true;
        }
        // Repair snapshots already damaged by an older concurrent writer.
        var recent = launch.RecentTargets.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(LaunchSettings.MaxRecentTargets).ToImmutableList();
        if (!launch.RecentTargets.SequenceEqual(recent)) changed = true;
        launch = launch with { RecentTargets = recent };
        var automation = NormalizePasswords(settings.Automation ?? new AutomationSettings(), defaultAccount, out var passwordsChanged);
        settings = settings with
        {
            Session = settings.Session ?? new SessionSettings(), Launch = launch,
            Window = settings.Window ?? new WindowSettings(), Automation = automation
        };
        changed |= passwordsChanged;
        if (settings.SettingsVersion >= AppSettings.CurrentVersion) return changed;

        // Version 1 changed the default from smart sizing to native resolution.
        if (settings.SettingsVersion < 1) settings = settings with { Session = settings.Session with { SmartSizing = false } };
        if (settings.SettingsVersion < 2)
        {
            settings = settings with
            {
                Session = settings.Session with { AutomaticDesktopSize = true, SmartSizing = true },
                Window = settings.Window with { SmallWindowMode = false }
            };
        }
        settings = settings with { SettingsVersion = AppSettings.CurrentVersion };
        return true;
    }

    private static AutomationSettings NormalizePasswords(AutomationSettings automation, string defaultAccount, out bool changed)
    {
        changed = automation.AutoLoginPasswords is null;
        var normalized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (automation.AutoLoginPasswords is not null)
        {
            // Invalid files may contain case collisions. Resolve independently of hash order.
            foreach (var pair in automation.AutoLoginPasswords.OrderByDescending(pair => pair.Key, StringComparer.Ordinal))
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
        var legacy = automation.LegacyAutoLoginPassword;
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            var account = string.IsNullOrWhiteSpace(automation.AutoLoginUserName)
                ? defaultAccount : automation.AutoLoginUserName.Trim();
            normalized.TryAdd(account, legacy);
            changed = true;
        }
        return automation with { AutoLoginPasswords = normalized.ToImmutable(), LegacyAutoLoginPassword = null };
    }
}
