using DesktopTwin.Models;
using DesktopTwin.Contracts;
using DesktopTwin.Services;
using NetArchTest.Rules;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace DesktopTwin.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void PublishedSettingsGraphOnlyContainsImmutableValues()
    {
        var pending = new Queue<Type>([typeof(AppSettings)]);
        var visited = new HashSet<Type>();
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type)) continue;
            Assert.True(type.IsSealed, $"Published settings type must be sealed: {type}");
            foreach (var property in type.GetProperties())
            {
                var setter = property.SetMethod;
                Assert.True(setter is null || setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)),
                    $"Published settings property must be init-only: {type}.{property.Name}");
                var valueType = property.PropertyType;
                if (valueType.IsPrimitive || valueType == typeof(string)) continue;
                if (valueType.IsGenericType)
                {
                    var generic = valueType.GetGenericTypeDefinition();
                    Assert.True(generic == typeof(ImmutableList<>) || generic == typeof(ImmutableDictionary<,>),
                        $"Mutable collection in settings graph: {type}.{property.Name}");
                    Assert.All(valueType.GenericTypeArguments, argument => Assert.Equal(typeof(string), argument));
                    continue;
                }
                Assert.Equal("DesktopTwin.Models", valueType.Namespace);
                pending.Enqueue(valueType);
            }
        }
    }

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
    public void SessionLifecycleDoesNotDependOnWpfOrWindowsImplementations()
    {
        var assembly = typeof(AppSettings).Assembly;
        foreach (var type in new[] { typeof(ChildSessionService), typeof(SessionConnection), typeof(SessionLogoffOperation),
                     typeof(IChildSessionPlatform) })
        {
            var result = Types.InAssembly(assembly).That().HaveName(type.Name).ShouldNot()
                .HaveDependencyOnAny("DesktopTwin.View", "DesktopTwin.ViewModel", "DesktopTwin.Interop",
                    "System.Windows", "Microsoft.Win32", "DesktopTwin.Services.AutoLoginService",
                    "DesktopTwin.Services.ChildSessionEnvironmentCheck").GetResult();
            Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
        }
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
