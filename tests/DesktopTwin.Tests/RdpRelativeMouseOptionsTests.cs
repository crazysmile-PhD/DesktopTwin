using System.Reflection;
using System.Runtime.InteropServices;
using DesktopTwin.Interop;

namespace DesktopTwin.Tests;

public sealed class RdpRelativeMouseOptionsTests
{
    [Fact]
    public void LoginRestoresModeAfterConnectionResetsThePreconnectionValue()
    {
        bool allow = false, relative = false;
        Assert.True(RdpRelativeMouseOptions.Apply(true, value => allow = value, value => relative = value));
        relative = false; // The connection established a new native input context.
        var result = RdpRelativeMouseOptions.ReapplyAfterLogin(true, value => relative = value, () => relative);
        Assert.True(result);
        Assert.True(allow);
        Assert.True(relative);
    }

    [Fact]
    public void LoginReadbackDoesNotTreatAcceptedWriteAsRelativeInputEvidence()
    {
        var result = RdpRelativeMouseOptions.ReapplyAfterLogin(true, _ => { }, () => false);
        Assert.False(result);
    }

    [Fact]
    public void DisabledModeIsAlsoAppliedAfterLogin()
    {
        var relative = true;
        Assert.False(RdpRelativeMouseOptions.ReapplyAfterLogin(false, value => relative = value, () => relative));
        Assert.False(relative);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedPostLoginWriteOrReadKeepsTheConnectionUsable(bool failRead)
    {
        var result = RdpRelativeMouseOptions.ReapplyAfterLogin(true,
            _ => { if (!failRead) throw new COMException("write unsupported"); },
            () => throw new TargetInvocationException(new COMException("read unsupported")));
        Assert.Null(result);
    }

    [Fact]
    public void PostLoginDoesNotHideProgrammingErrors()
    {
        Assert.Throws<InvalidOperationException>(() => RdpRelativeMouseOptions.ReapplyAfterLogin(true,
            _ => throw new InvalidOperationException("programming error"), () => true));
    }

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
