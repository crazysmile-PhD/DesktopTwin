using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionLaunchIntegrationTests : IClassFixture<SessionLaunchFixture>
{
    private readonly SessionLaunchFixture _fixture;
    public SessionLaunchIntegrationTests(SessionLaunchFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task UpgradeRemovesOnlyTheRetiredAgentAndItsStartupEntry()
    {
        var legacyDirectory = Path.Combine(_fixture.Directory, "legacy");
        var unrelatedDirectory = Path.Combine(_fixture.Directory, "unrelated");
        Directory.CreateDirectory(legacyDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        var retired = Path.Combine(legacyDirectory, "DesktopTwin.Agent.exe");
        var unrelated = Path.Combine(unrelatedDirectory, "DesktopTwin.Agent.exe");
        File.Copy(_fixture.Target, retired);
        File.Copy(_fixture.Target, unrelated);
        File.WriteAllText(Path.Combine(legacyDirectory, "uu-switch-request.json"), "old request");
        File.WriteAllText(Path.Combine(legacyDirectory, "session-agent-heartbeat.txt"), "old heartbeat");
        var retainedSettings = Path.Combine(legacyDirectory, "settings.json");
        File.WriteAllText(retainedSettings, "retained settings");
        var keyName = @"Software\DesktopTwinTests\" + Guid.NewGuid().ToString("N");
        using var retiredProcess = Process.Start(new ProcessStartInfo(retired, "hidden")
            { UseShellExecute = false, CreateNoWindow = true })!;
        using var unrelatedProcess = Process.Start(new ProcessStartInfo(unrelated, "hidden")
            { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            using var startup = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyName);
            startup.SetValue("DesktopTwinSessionAgent", retired);
            startup.SetValue("OtherStartup", "retained startup");
            LegacyUuAgentCleanup.Remove(legacyDirectory, startup);
            await retiredProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(unrelatedProcess.HasExited);
            Assert.Null(startup.GetValue("DesktopTwinSessionAgent"));
            Assert.Equal("retained startup", startup.GetValue("OtherStartup"));
            Assert.False(File.Exists(retired));
            Assert.False(File.Exists(Path.Combine(legacyDirectory, "uu-switch-request.json")));
            Assert.False(File.Exists(Path.Combine(legacyDirectory, "session-agent-heartbeat.txt")));
            Assert.Equal("retained settings", File.ReadAllText(retainedSettings));
            Assert.True(File.Exists(_fixture.Worker));
            LegacyUuAgentCleanup.Remove(legacyDirectory, startup); // Repeated upgrades are harmless.
        }
        finally
        {
            foreach (var process in new[] { retiredProcess, unrelatedProcess })
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
            }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task WorkerRejectsRetiredHotkeyMode()
    {
        using var worker = Process.Start(new ProcessStartInfo(_fixture.Worker, "--hotkey Ctrl+Alt+U")
            { UseShellExecute = false, CreateNoWindow = true })!;
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, worker.ExitCode);
    }

    [Fact]
    public async Task RealWorkerConfirmsResponsiveWindow()
    {
        await WithWorker(async channel =>
        {
            await channel.LaunchAndConfirmAsync(_fixture.Target, "window", _fixture.Directory, default);
        });
    }

    [Fact]
    public async Task ImmediateExitIsNotReportedAsSuccess()
    {
        await WithWorker(async channel =>
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                channel.LaunchAndConfirmAsync(_fixture.Target, "exit", _fixture.Directory, default));
            Assert.Contains("退出码 7", error.Message);
        });
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("hung")]
    public async Task LiveProcessWithoutResponsiveVisibleWindowIsNotSuccess(string mode)
    {
        await WithWorker(async channel =>
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                channel.LaunchAndConfirmAsync(_fixture.Target, mode, _fixture.Directory, default, 600));
            Assert.Contains("未在确认期限内", error.Message);
        });
    }

    [Fact]
    public async Task DisconnectBeforeAuthorizationDoesNotLaunchTarget()
    {
        using var channel = new SessionLaunchChannel(_fixture.Worker, _fixture.SessionId);
        using var worker = _fixture.StartWorker(channel.PipeName);
        await channel.ConnectAsync(default);
        channel.Dispose();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(Process.GetProcessesByName("LaunchFixture"), process =>
        {
            using (process) return string.Equals(process.MainModule?.FileName, _fixture.Target, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task CancellationClosesChannelAndStopsWorker()
    {
        using var channel = new SessionLaunchChannel(_fixture.Worker, _fixture.SessionId);
        using var worker = _fixture.StartWorker(channel.PipeName);
        await channel.ConnectAsync(default);
        using var cancel = new CancellationTokenSource(250);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.LaunchAndConfirmAsync(_fixture.Target, "hidden", _fixture.Directory, cancel.Token));
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        _fixture.KillTargets();
    }

    [Fact]
    public async Task RealSchedulerDeletesTaskBeforeTargetStarts()
    {
        var prefix = "DesktopTwin-SessionLaunch-Test" + Guid.NewGuid().ToString("N") + "-";
        await Task.Run(() => ChildSessionProcessLauncher.LaunchWithTemporaryTaskAsync(
            _fixture.SessionId, _fixture.Target, "check-tasks " + prefix, _fixture.Directory, false,
            prefix, () => _fixture.SessionId, default));
        Assert.Equal("0", File.ReadAllText(Path.Combine(_fixture.Directory, "tasks-at-target-start.txt")));
        // The scheduler worker exits after its receipt; deleting its definition must not
        // tear down the independently launched target when the worker subsequently exits.
        await Task.Delay(300);
        var targets = Process.GetProcessesByName("LaunchFixture");
        try { Assert.Contains(targets, process => string.Equals(process.MainModule?.FileName,
            _fixture.Target, StringComparison.OrdinalIgnoreCase) && !process.HasExited); }
        finally { foreach (var target in targets) target.Dispose(); }
        _fixture.KillTargets();
    }

    private async Task WithWorker(Func<SessionLaunchChannel, Task> operation)
    {
        using var channel = new SessionLaunchChannel(_fixture.Worker, _fixture.SessionId);
        using var worker = _fixture.StartWorker(channel.PipeName);
        try
        {
            await channel.ConnectAsync(default);
            await operation(channel);
        }
        finally
        {
            channel.Dispose();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            _fixture.KillTargets();
        }
    }
}

public sealed class SessionLaunchFixture : IDisposable
{
    internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "DesktopTwin-launch-test-" + Guid.NewGuid().ToString("N"));
    internal string Target => Path.Combine(Directory, "LaunchFixture.exe");
    internal string Worker { get; }
    internal uint SessionId { get; } = (uint)Process.GetCurrentProcess().SessionId;

    public SessionLaunchFixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var source = Path.Combine(Directory, "LaunchFixture.cs");
        File.WriteAllText(source, """
            using System;
            using System.Diagnostics;
            using System.Drawing;
            using System.IO;
            using System.Threading;
            using System.Windows.Forms;
            class Target {
                [STAThread] static int Main(string[] args) {
                    if (args[0] == "exit") return 7;
                    if (args[0] == "hidden") { Thread.Sleep(60000); return 0; }
                    if (args[0] == "check-tasks") {
                        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                        scheduler.Connect();
                        dynamic tasks = scheduler.GetFolder("\\").GetTasks(1);
                        int count = 0;
                        for (int i = 1; i <= tasks.Count; i++)
                            if (((string)tasks.Item(i).Name).StartsWith(args[1])) count++;
                        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                            "tasks-at-target-start.txt"), count.ToString());
                    }
                    Application.Run(new TargetForm(args[0] == "hung"));
                    return 0;
                }
            }
            class TargetForm : Form {
                bool hung;
                public TargetForm(bool value) {
                    hung = value; Text = "DesktopTwin launch test"; ShowInTaskbar = false;
                    StartPosition = FormStartPosition.Manual; Location = new Point(-20000, -20000);
                    Size = new Size(80, 60);
                }
                protected override void WndProc(ref Message message) {
                    if (hung && message.Msg == 0) Thread.Sleep(60000);
                    base.WndProc(ref message);
                }
            }
            """);
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        using var compile = Process.Start(new ProcessStartInfo(compiler,
            $"/nologo /target:winexe /out:\"{Target}\" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll \"{source}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var output = compile.StandardOutput.ReadToEnd();
        compile.WaitForExit();
        if (compile.ExitCode != 0) throw new InvalidOperationException(output);
        Worker = SessionLaunchChannel.EnsureWorkerInstalled(Path.Combine(Directory, "worker"));
    }

    internal Process StartWorker(string pipe) => Process.Start(new ProcessStartInfo(Worker,
        $"--launch-worker {pipe} {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true })!;

    internal void KillTargets()
    {
        foreach (var process in Process.GetProcessesByName("LaunchFixture"))
        {
            using (process)
            {
                if (string.Equals(process.MainModule?.FileName, Target, StringComparison.OrdinalIgnoreCase))
                { process.Kill(); process.WaitForExit(5000); }
            }
        }
    }

    public void Dispose()
    {
        KillTargets();
        System.IO.Directory.Delete(Directory, true);
    }
}
