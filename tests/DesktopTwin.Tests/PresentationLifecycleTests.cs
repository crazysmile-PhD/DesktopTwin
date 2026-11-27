using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.IO;
using DesktopTwin.Services;
using DesktopTwin.ViewModel;

namespace DesktopTwin.Tests;

[Collection(WindowsDesktopTestGroup.Name)]
public sealed class PresentationLifecycleTests
{
    [Fact]
    public void DisposedViewModelIsReleasedWhileTheSessionServiceLives() => RunOnSta(() =>
    {
        using var rig = new SessionTestRig();
        var reference = CreateDisposedViewModel(rig);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(reference.IsAlive);
        rig.Service.SetTopmost(!rig.Service.TopmostEnabled);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedViewModel(SessionTestRig rig)
    {
        var viewModel = new MainWindowViewModel(rig.Service, rig.Settings);
        viewModel.Dispose();
        viewModel.Dispose();
        return new WeakReference(viewModel);
    }

    [Fact]
    public async Task HiddenUnconnectedFullscreenDoesNotScanForCredentials()
    {
        // WPF Application.Shutdown is process-wide; keep it out of the shared native test host.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Missing test configuration directory.");
        var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "DesktopTwin.NativeSmoke", "bin", configuration, "net10.0-windows", "DesktopTwin.NativeSmoke.dll"));
        Assert.True(File.Exists(probe), "Build the presentation probe with the test project.");
        var result = await BoundedProcessRunner.RunAsync("dotnet", $"\"{probe}\" --presentation-lifecycle", TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.Output ?? "Presentation probe failed or exceeded its deadline.");
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Presentation lifecycle did not complete.");
        failure?.Throw();
    }
}
