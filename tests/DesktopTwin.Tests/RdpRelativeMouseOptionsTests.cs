using System.Reflection;
using System.Runtime.InteropServices;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpRelativeMouseOptionsTests
{
    [Fact]
    public void DisablingOnReusedControlClearsBothPreviouslyEnabledProperties()
    {
        bool allow = false, relative = false;
        Assert.True(RdpRelativeMouseOptions.Apply(true, value => allow = value, value => relative = value));
        Assert.True(allow);
        Assert.True(relative);

        Assert.True(RdpRelativeMouseOptions.Apply(false, value => allow = value, value => relative = value));
        Assert.False(allow);
        Assert.False(relative);
    }

    [Fact]
    public void UnsupportedCapabilityDoesNotEnableLegacyModeAndStillResetsStaleState()
    {
        var modes = new List<bool>();
        var accepted = RdpRelativeMouseOptions.Apply(true,
            _ => throw new COMException("unsupported", unchecked((int)0x80004001)), modes.Add);

        Assert.False(accepted);
        Assert.Equal(new[] { false }, modes);
    }

    [Fact]
    public void RejectedModeRollsBackCapabilityBeforeContinuingConnection()
    {
        bool allow = false, relative = false;
        var accepted = RdpRelativeMouseOptions.Apply(true, value => allow = value, value =>
        {
            if (value) throw new COMException("unsupported");
            relative = value;
        });

        Assert.False(accepted);
        Assert.False(allow);
        Assert.False(relative);
    }

    [Fact]
    public void ReflectionWrappedComFailureDoesNotEscapeOrLeaveCapabilityEnabled()
    {
        bool allow = false;
        var accepted = RdpRelativeMouseOptions.Apply(true, value => allow = value, value =>
        {
            if (value) throw new TargetInvocationException(new COMException("unsupported"));
        });

        Assert.False(accepted);
        Assert.False(allow);
    }

    [Fact]
    public void FailedModeResetStillAttemptsCapabilityResetAndReportsFailure()
    {
        var allowWrites = new List<bool>();
        var accepted = RdpRelativeMouseOptions.Apply(false, allowWrites.Add,
            _ => throw new COMException("reset failed"));

        Assert.False(accepted);
        Assert.Equal(new[] { false }, allowWrites);
    }

    [Fact]
    public void FailedRollbackDoesNotReportRequestAsAccepted()
    {
        bool allow = false;
        var accepted = RdpRelativeMouseOptions.Apply(true, value => allow = value,
            _ => throw new COMException("all mode writes fail"));

        Assert.False(accepted);
        Assert.False(allow);
    }

    [Fact]
    public void UnexpectedProgrammingFailureIsNotHiddenAsUnsupportedCapability()
    {
        Assert.Throws<InvalidOperationException>(() => RdpRelativeMouseOptions.Apply(true,
            _ => throw new InvalidOperationException("programming error"), _ => { }));
    }
}
