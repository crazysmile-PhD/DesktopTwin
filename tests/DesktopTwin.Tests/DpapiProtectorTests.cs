using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class DpapiProtectorTests
{
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
