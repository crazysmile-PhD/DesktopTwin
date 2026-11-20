using DesktopTwin.Models;
using DesktopTwin.Services;
using NetArchTest.Rules;

namespace DesktopTwin.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void SettingsModelsRemainIndependentOfUiStorageAndServices()
    {
        var result = Types.InAssembly(typeof(AppSettings).Assembly).That()
            .ResideInNamespace("DesktopTwin.Models").ShouldNot()
            .HaveDependencyOnAny("DesktopTwin.Services", "DesktopTwin.View", "DesktopTwin.ViewModel",
                "DesktopTwin.Interop", "System.Windows", "System.IO").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void DiagnosticHistoryRemainsIndependentOfUiAndNativeOperations()
    {
        var result = Types.InAssembly(typeof(AppSettings).Assembly).That()
            .HaveName(nameof(DiagnosticIssueHistory)).ShouldNot()
            .HaveDependencyOnAny("DesktopTwin.View", "DesktopTwin.ViewModel", "DesktopTwin.Interop",
                "System.Windows", "System.IO").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void NewServicesCannotDependOnConcreteViews()
    {
        // Existing composition seams are explicit debt. New services must keep UI outside.
        var result = Types.InAssembly(typeof(AppSettings).Assembly).That()
            .ResideInNamespace("DesktopTwin.Services")
            .And().DoNotHaveName(nameof(ChildSessionService))
            .And().DoNotHaveName(nameof(TrayIconService)).ShouldNot()
            .HaveDependencyOnAny("DesktopTwin.View", "DesktopTwin.ViewModel").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
