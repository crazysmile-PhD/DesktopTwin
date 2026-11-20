using DesktopTwin.Services;
using Issue = DesktopTwin.Services.ChildSessionEnvironmentCheck.Issue;
using Severity = DesktopTwin.Services.ChildSessionEnvironmentCheck.Severity;

namespace DesktopTwin.Tests;

public sealed class DiagnosticIssueHistoryTests
{
    private static Issue Problem(string id, Severity severity = Severity.Warning, string title = "problem") =>
        new(id, title, "summary", "detail", severity, true, false);

    [Fact]
    public void ResolvedProblemKeepsTitleAndReappearanceRevokesResolution()
    {
        var history = new DiagnosticIssueHistory();
        history.Update([Problem("issue")], true);
        Assert.Empty(history.ResolvedIds);
        history.Update([], true);
        Assert.Contains("issue", history.ResolvedIds);
        Assert.Equal("problem", history.GetTitle("issue"));
        history.Update([Problem("issue", title: "new title")], true);
        Assert.Empty(history.ResolvedIds);
        Assert.Equal("new title", history.GetTitle("issue"));
        Assert.Equal("unknown", history.GetTitle("unknown"));
    }

    [Fact]
    public void InactiveChecksAreNeverRecordedAsPreviouslyBroken()
    {
        var history = new DiagnosticIssueHistory();
        history.Update([Problem("inactive", Severity.Inactive)], true);
        history.Update([], true);
        Assert.Empty(history.ResolvedIds);
    }

    [Fact]
    public void MissingIntegrityEvidenceCannotCreateOrRetainResolvedState()
    {
        const string id = "termsrv-dll-tampered";
        var history = new DiagnosticIssueHistory();
        history.Update([Problem(id), Problem("other")], false);
        history.Update([], false);
        Assert.DoesNotContain(id, history.ResolvedIds);
        Assert.Contains("other", history.ResolvedIds);
        history.Update([], true);
        Assert.Contains(id, history.ResolvedIds);
        history.Update([], false);
        Assert.DoesNotContain(id, history.ResolvedIds);
    }
}
