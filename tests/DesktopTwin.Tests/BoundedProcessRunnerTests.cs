using System.Diagnostics;
using System.IO;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class BoundedProcessRunnerTests : IClassFixture<CommandFixture>
{
    private readonly CommandFixture _fixture;
    public BoundedProcessRunnerTests(CommandFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LargeOutputIsDrainedWhileTheProcessRunsAndExitCodeIsPreserved()
    {
        var result = await BoundedProcessRunner.RunAsync(_fixture.Executable, "output", TimeSpan.FromSeconds(15))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(new string('x', 256 * 1024), result.Output);
    }

    [Fact]
    public async Task DeadlineIncludesOutputReadAndStopsTheRunningProcessTree()
    {
        var marker = _fixture.NewMarker();
        var elapsed = Stopwatch.StartNew();
        try
        {
            var result = await BoundedProcessRunner.RunAsync(_fixture.Executable, $"tree \"{marker}\"", TimeSpan.FromSeconds(3))
                .WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(-1, result.ExitCode);
            Assert.Null(result.Output);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Command exceeded deadline and cleanup budget: {elapsed.Elapsed}");
            Assert.True(File.Exists(marker), "Fixture must start both processes before the deadline.");
            var ids = File.ReadAllLines(marker).Select(int.Parse).ToArray();
            Assert.Equal(2, ids.Length);
            foreach (var id in ids) Assert.True(CommandFixture.HasExited(id), $"Owned process {id} survived the timeout.");
        }
        finally { _fixture.StopRecordedProcesses(marker); }
    }

    [Fact]
    public async Task DeadlineAlsoBoundsOutputInheritedByAChildAfterTheParentExits()
    {
        var marker = _fixture.NewMarker();
        try
        {
            var result = await BoundedProcessRunner.RunAsync(_fixture.Executable, $"orphan \"{marker}\"", TimeSpan.FromSeconds(3))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(-1, result.ExitCode);
            Assert.Null(result.Output);
            Assert.True(File.Exists(marker));
        }
        finally { _fixture.StopRecordedProcesses(marker); }
    }

    [Fact]
    public async Task FailedStartReturnsFailure()
    {
        var result = await BoundedProcessRunner.RunAsync(Path.Combine(_fixture.Directory, "missing.exe"), "", TimeSpan.FromSeconds(1));
        Assert.Equal((null, -1), result);
    }
}

public sealed class CommandFixture : IDisposable
{
    internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "DesktopTwin-command-test-" + Guid.NewGuid().ToString("N"));
    internal string Executable => Path.Combine(Directory, "CommandFixture.exe");

    public CommandFixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var source = Path.Combine(Directory, "CommandFixture.cs");
        File.WriteAllText(source, """
            using System;
            using System.Diagnostics;
            using System.IO;
            using System.Threading;
            class CommandFixture {
                static int Main(string[] args) {
                    if (args[0] == "output") { Console.Write(new string('x', 256 * 1024)); return 7; }
                    if (args[0] == "sleep") { Thread.Sleep(60000); return 0; }
                    var start = new ProcessStartInfo(typeof(CommandFixture).Assembly.Location, "sleep");
                    start.UseShellExecute = false; start.CreateNoWindow = true;
                    using (var child = Process.Start(start)) {
                        File.WriteAllLines(args[1], new[] { Process.GetCurrentProcess().Id.ToString(), child.Id.ToString() });
                        if (args[0] == "orphan") return 0;
                        Thread.Sleep(60000);
                    }
                    return 0;
                }
            }
            """);
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        using var compile = Process.Start(new ProcessStartInfo(compiler,
            $"/nologo /target:exe /out:\"{Executable}\" \"{source}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var output = compile.StandardOutput.ReadToEnd();
        if (!compile.WaitForExit(30_000)) { compile.Kill(entireProcessTree: true); throw new TimeoutException("Fixture compilation timed out."); }
        if (compile.ExitCode != 0) throw new InvalidOperationException(output);
    }

    internal string NewMarker() => Path.Combine(Directory, Guid.NewGuid().ToString("N") + ".pids");
    internal static bool HasExited(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    internal void StopRecordedProcesses(string marker)
    {
        if (!File.Exists(marker)) return;
        foreach (var id in File.ReadAllLines(marker).Select(int.Parse))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.HasExited || !string.Equals(process.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000)) throw new TimeoutException("Owned fixture process did not exit.");
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) when (HasExited(id)) { }
            catch (System.ComponentModel.Win32Exception) when (HasExited(id)) { }
        }
        File.Delete(marker);
    }

    public void Dispose()
    {
        foreach (var marker in System.IO.Directory.EnumerateFiles(Directory, "*.pids")) StopRecordedProcesses(marker);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory)) File.Delete(file);
        System.IO.Directory.Delete(Directory);
    }
}
