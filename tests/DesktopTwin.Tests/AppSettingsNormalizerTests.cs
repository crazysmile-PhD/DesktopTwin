using System.Collections.Immutable;
using DesktopTwin.Models;

namespace DesktopTwin.Tests;

public sealed class AppSettingsNormalizerTests
{
    [Fact]
    public void MigrationIsIdempotentAndPreservesNewerPassword()
    {
        var settings = new AppSettings
        {
            Session = new() { SmartSizing = true },
            Automation = new()
            {
                AutoLoginUserName = "USER", LegacyAutoLoginPassword = "old",
                AutoLoginPasswords = ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase).Add("user", "new")
            }
        };

        Assert.True(AppSettingsNormalizer.NormalizeAndMigrate(ref settings, "fallback"));
        Assert.True(settings.Session.SmartSizing);
        Assert.True(settings.Session.AutomaticDesktopSize);
        Assert.Equal("new", settings.Automation.AutoLoginPasswords["USER"]);
        Assert.Null(settings.Automation.LegacyAutoLoginPassword);
        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.False(AppSettingsNormalizer.NormalizeAndMigrate(ref settings, "fallback"));
    }

    [Fact]
    public void FutureVersionIsNotDowngradedAndUserChoicesRemain()
    {
        var settings = new AppSettings
        {
            SettingsVersion = AppSettings.CurrentVersion + 10,
            Session = new() { SmartSizing = true }
        };
        Assert.False(AppSettingsNormalizer.NormalizeAndMigrate(ref settings, "fallback"));
        Assert.True(settings.Session.SmartSizing);
        Assert.Equal(AppSettings.CurrentVersion + 10, settings.SettingsVersion);
    }

    [Fact]
    public void MalformedPasswordsAreRemovedAndCaseCollisionsAreDeterministic()
    {
        var settings = new AppSettings
        {
            SettingsVersion = AppSettings.CurrentVersion,
            Automation = new()
            {
                AutoLoginPasswords = new Dictionary<string, string>
                {
                    [" "] = "blank", ["invalid"] = null!, ["user"] = "first", ["USER"] = "last"
                }.ToImmutableDictionary()
            }
        };
        Assert.True(AppSettingsNormalizer.NormalizeAndMigrate(ref settings, "fallback"));
        Assert.Single(settings.Automation.AutoLoginPasswords);
        Assert.Equal("last", settings.Automation.AutoLoginPasswords["User"]);
        Assert.False(AppSettingsNormalizer.NormalizeAndMigrate(ref settings, "fallback"));
    }
}
