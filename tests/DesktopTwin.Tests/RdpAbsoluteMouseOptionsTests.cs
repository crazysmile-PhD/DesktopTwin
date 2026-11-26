using System.Reflection;
using System.Runtime.InteropServices;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpAbsoluteMouseOptionsTests
{
    [Fact]
    public void ReceiverOwnsRelativeMotionAndNativeRdpAlwaysResetsBothProperties()
    {
        var writes = new List<(string, bool)>();
        Assert.True(RdpAbsoluteMouseOptions.Reset(value => writes.Add(("allow", value)),
            value => writes.Add(("mode", value))));
        Assert.Equal(new[] { ("mode", false), ("allow", false) }, writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedOptionalPropertyDoesNotSkipTheOtherReset(bool failMode)
    {
        var calls = new List<string>();
        Assert.False(RdpAbsoluteMouseOptions.Reset(value =>
        {
            calls.Add("allow");
            if (!failMode) throw new COMException("unsupported");
        }, value =>
        {
            calls.Add("mode");
            if (failMode) throw new TargetInvocationException(new COMException("unsupported"));
        }));
        Assert.Equal(new[] { "mode", "allow" }, calls);
    }

    [Fact]
    public void ResetDoesNotHideProgrammingErrors() =>
        Assert.Throws<InvalidOperationException>(() => RdpAbsoluteMouseOptions.Reset(_ => { },
            _ => throw new InvalidOperationException("programming error")));
}
