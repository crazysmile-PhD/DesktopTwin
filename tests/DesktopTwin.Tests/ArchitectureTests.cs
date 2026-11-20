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
    public void NoServiceCanDependOnConcreteViews()
    {
        var result = Types.InAssembly(typeof(AppSettings).Assembly).That()
            .ResideInNamespace("DesktopTwin.Services").ShouldNot()
            .HaveDependencyOnAny("DesktopTwin.View", "DesktopTwin.ViewModel").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void SessionContractsRemainIndependentOfImplementations()
    {
        var result = Types.InAssembly(typeof(AppSettings).Assembly).That()
            .ResideInNamespace("DesktopTwin.Contracts").ShouldNot()
            .HaveDependencyOnAny("DesktopTwin.Services", "DesktopTwin.View", "DesktopTwin.ViewModel",
                "DesktopTwin.Interop", "System.Windows").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void DesktopHostDoesNotDependOnUiOrNativeControl()
    {
        var result = Types.InAssembly(typeof(AppSettings).Assembly).That()
            .HaveName(nameof(DesktopSessionHost)).ShouldNot()
            .HaveDependencyOnAny("DesktopTwin.View", "DesktopTwin.ViewModel", "DesktopTwin.Interop", "System.Windows").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void CredentialPolicyDoesNotDependOnUiStorageOrNativePlatform()
    {
        var result = Types.InAssembly(typeof(AppSettings).Assembly).That()
            .ResideInNamespace("DesktopTwin.Credentials").ShouldNot()
            .HaveDependencyOnAny("DesktopTwin.Services", "DesktopTwin.View", "DesktopTwin.ViewModel",
                "DesktopTwin.Interop", "System.Windows", "Microsoft.Win32", "System.IO",
                "System.Runtime.InteropServices").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
