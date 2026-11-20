using System.IO;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class DpapiProtectorTests
{
    [Fact]
    public void AutoLoginPersistsOnlyProtectedValues()
    {
        const string plain = "DesktopTwin-CI-synthetic-secret-42";
        var directory = Path.Combine(Path.GetTempPath(), "DesktopTwin-credentials-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var settings = new SettingsService(directory);
            using var autoLogin = new AutoLoginService(settings);
            settings.Update(s => s with { Automation = s.Automation with { AutoLoginUserName = "synthetic-account" } });
            Assert.True(autoLogin.SavePassword(plain));
            Assert.True(autoLogin.HasStoredPassword);
            var cipher = settings.Current.Automation.AutoLoginPasswords["synthetic-account"];
            Assert.NotEqual(plain, cipher);
            Assert.Equal(plain, DpapiProtector.Unprotect(cipher));
            settings.Flush();
            Assert.DoesNotContain(plain, File.ReadAllText(settings.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                File.Delete(Path.Combine(directory, "settings.json"));
                File.Delete(Path.Combine(directory, "settings.json.tmp"));
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public void SyntheticSecretRoundTripsThroughWindowsProtection()
    {
        const string plain = "DesktopTwin-CI-synthetic-secret-42";
        var cipher = DpapiProtector.Protect(plain);
        Assert.False(string.IsNullOrWhiteSpace(cipher));
        Assert.NotEqual(plain, cipher);
        Assert.Equal(plain, DpapiProtector.Unprotect(cipher));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64")]
    [InlineData("AQID")]
    public void InvalidCiphertextIsRejected(string? cipher) => Assert.Null(DpapiProtector.Unprotect(cipher));
}
