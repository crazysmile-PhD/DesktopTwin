namespace DesktopTwin.Tests;

/// <summary>
/// Native windows, ActiveX and visible process probes share the runner's desktop and WinForms state.
/// Keep their assertions and deadlines intact while isolating them from parallel test collections.
/// Pure lifecycle and protocol tests retain the runner's normal parallel scheduling.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsDesktopTestGroup
{
    /// <summary>The xUnit collection name for shared native desktop resources.</summary>
    public const string Name = "Windows desktop";
}
