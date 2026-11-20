using DesktopTwin.Models;

namespace DesktopTwin.Tests;

public sealed class AppSettingsNormalizerTests
{
    [Fact]
    public void MigrationIsIdempotentAndPreservesNewerPassword()
    {
        var settings = new AppSettings();
        settings.Session.SmartSizing = true;
        settings.Automation.AutoLoginUserName = "USER";
        settings.Automation.LegacyAutoLoginPassword = "old";
        settings.Automation.AutoLoginPasswords["user"] = "new";

        Assert.True(AppSettingsNormalizer.NormalizeAndMigrate(settings, "fallback"));
        Assert.True(settings.Session.SmartSizing);
        Assert.True(settings.Session.AutomaticDesktopSize);
        Assert.Equal("new", settings.Automation.AutoLoginPasswords["USER"]);
        Assert.Null(settings.Automation.LegacyAutoLoginPassword);
        Assert.Equal(AppSettings.CurrentVersion, settings.SettingsVersion);
        Assert.False(AppSettingsNormalizer.NormalizeAndMigrate(settings, "fallback"));
    }

    [Fact]
    public void FutureVersionIsNotDowngradedAndUserChoicesRemain()
    {
        var settings = new AppSettings { SettingsVersion = AppSettings.CurrentVersion + 10 };
        settings.Session.SmartSizing = true;
        Assert.False(AppSettingsNormalizer.NormalizeAndMigrate(settings, "fallback"));
        Assert.True(settings.Session.SmartSizing);
        Assert.Equal(AppSettings.CurrentVersion + 10, settings.SettingsVersion);
    }

    [Fact]
    public void MalformedPasswordsAreRemovedAndCaseCollisionsAreDeterministic()
    {
        var settings = new AppSettings { SettingsVersion = AppSettings.CurrentVersion };
        settings.Automation.AutoLoginPasswords = new Dictionary<string, string>
        {
            [" "] = "blank", ["invalid"] = null!, ["user"] = "first", ["USER"] = "last"
        };
        Assert.True(AppSettingsNormalizer.NormalizeAndMigrate(settings, "fallback"));
        Assert.Single(settings.Automation.AutoLoginPasswords);
        Assert.Equal("last", settings.Automation.AutoLoginPasswords["User"]);
        Assert.False(AppSettingsNormalizer.NormalizeAndMigrate(settings, "fallback"));
    }
}
