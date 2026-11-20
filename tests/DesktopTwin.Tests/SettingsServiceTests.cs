using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using DesktopTwin.Models;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 设置的持久化测试。覆盖「改完能读回来」与「文件损坏时不让程序起不来」两条主路径。
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory;

    public SettingsServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "desktop-twin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void 默认值符合预期()
    {
        using var service = new SettingsService(_directory);
        var settings = service.Current;

        Assert.Equal(1920, settings.Session.DesktopWidth);
        Assert.Equal(1080, settings.Session.DesktopHeight);

        // 显示比例默认 1:1（文字最清晰的一档）；自适应缩放需要用户主动选
        Assert.False(settings.Session.SmartSizing);
        Assert.False(settings.Session.RelativeMouseMode);
        Assert.True(settings.Session.KeepAspectRatio);
        Assert.True(settings.Launch.AutoLaunchOnConnect);

        // 提权默认关闭：减少与反作弊、账号安全机制的冲突
        Assert.False(settings.Launch.RunAsAdministrator);

        // 首次运行提示默认未确认
        Assert.False(settings.ChildSessionNoticeAccepted);

        Assert.True(settings.Automation.AutoLoginEnabled);

        // 密码表默认是空的，且按键不区分大小写（多账户各存一份）
        Assert.Empty(settings.Automation.AutoLoginPasswords);
        settings = settings with { Automation = settings.Automation with { AutoLoginPasswords = settings.Automation.AutoLoginPasswords.Add("SomeUser", "x") } };
        Assert.True(settings.Automation.AutoLoginPasswords.ContainsKey("someuser"));

        // 全屏顶栏默认不固定：只在鼠标到顶端时出现
        Assert.False(settings.Window.FullscreenBarPinned);
    }

    [Fact]
    public void 写入后能被新实例读回()
    {
        using (var writer = new SettingsService(_directory))
        {
            writer.Update(s => s with { Session = s.Session with { DesktopWidth = 2560 } });
            writer.Update(s => s with { Session = s.Session with { DesktopHeight = 1440 } });
            writer.Update(s => s with { Session = s.Session with { AudioMuted = true } });
            writer.Update(s => s with { Session = s.Session with { RelativeMouseMode = true } });
            writer.Update(s => s with { Launch = s.Launch with { TargetPath = @"C:\Games\game.exe" } });
            writer.Update(s => s with { Launch = s.Launch with { RunAsAdministrator = true } });
            writer.Update(s => s with { ChildSessionNoticeAccepted = true });
            writer.Update(s => s with { Window = s.Window with { FullscreenBarPinned = true } });
            writer.Flush();
        }

        using var reader = new SettingsService(_directory);
        Assert.Equal(2560, reader.Current.Session.DesktopWidth);
        Assert.Equal(1440, reader.Current.Session.DesktopHeight);
        Assert.True(reader.Current.Session.AudioMuted);
        Assert.True(reader.Current.Session.RelativeMouseMode);
        Assert.Equal(@"C:\Games\game.exe", reader.Current.Launch.TargetPath);
        Assert.True(reader.Current.Launch.RunAsAdministrator);
        Assert.True(reader.Current.ChildSessionNoticeAccepted);

        // 全屏顶栏的固定状态要能记住，否则每次进全屏都要重新点一次图钉。
        Assert.True(reader.Current.Window.FullscreenBarPinned);
    }

    [Fact]
    public void 设置文件损坏时回退到默认值()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ 这不是合法 JSON");

        using var service = new SettingsService(_directory);

        Assert.Equal(1920, service.Current.Session.DesktopWidth);
    }

    [Fact]
    public void NullSectionsAndCollectionsAreRepairedAndPersisted()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """
            { "SettingsVersion": 1, "Session": null, "Launch": null,
              "Window": null, "Automation": null }
            """);
        using (var service = new SettingsService(_directory))
        {
            Assert.NotNull(service.Current.Session);
            Assert.NotNull(service.Current.Launch.RecentTargets);
            Assert.NotNull(service.Current.Window);
            Assert.NotNull(service.Current.Automation.AutoLoginPasswords);
        }
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var section in new[] { "Session", "Launch", "Window", "Automation" })
            Assert.Equal(System.Text.Json.JsonValueKind.Object, json.RootElement.GetProperty(section).ValueKind);
        using var reloaded = new SettingsService(_directory);
        Assert.NotNull(reloaded.Current.Launch.RecentTargets);
    }

    [Fact]
    public void NullPasswordTableDoesNotLoseLegacyPassword()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """
            { "SettingsVersion": 1,
              "Launch": { "RecentTargets": null },
              "Automation": { "AutoLoginPasswords": null,
                "AutoLoginPassword": "legacy", "AutoLoginUserName": " Example " } }
            """);
        using var service = new SettingsService(_directory);
        Assert.Empty(service.Current.Launch.RecentTargets);
        Assert.Equal("legacy", service.Current.Automation.AutoLoginPasswords["EXAMPLE"]);
        Assert.Null(service.Current.Automation.LegacyAutoLoginPassword);
    }

    [Fact]
    public void FlushAfterDisposeHasNoSideEffects()
    {
        var service = new SettingsService(_directory);
        service.Dispose();
        Assert.Throws<ObjectDisposedException>(service.Flush);
        Assert.False(File.Exists(service.SettingsPath));
    }

    [Fact]
    public async Task DeferredSavePersistsTheLatestChange()
    {
        using var service = new SettingsService(_directory);
        service.Update(s => s with { Session = s.Session with { DesktopWidth = 2560 } });
        service.Update(s => s with { Session = s.Session with { DesktopWidth = 3840 } });
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(service.SettingsPath)) await Task.Delay(25, timeout.Token);
        using var reader = new SettingsService(_directory);
        Assert.Equal(3840, reader.Current.Session.DesktopWidth);
    }

    [Fact]
    public void UpdateAfterDisposeDoesNotPublishOrScheduleSave()
    {
        var service = new SettingsService(_directory);
        service.Dispose();
        var before = service.Current;
        service.Update(_ => throw new InvalidOperationException("Late callback must not execute."));
        Assert.Same(before, service.Current);
        service.Dispose();
        Assert.False(File.Exists(service.SettingsPath));
    }

    [Fact]
    public async Task PublishedSettingsRemainImmutableAndUpdatesAreAtomic()
    {
        using var service = new SettingsService(_directory);
        var before = service.Current;
        using var building = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        var update = Task.Run(() => service.Update(s =>
        {
            var next = s with { Session = s.Session with { DesktopWidth = 2560 } };
            building.Set();
            Assert.True(publish.Wait(TimeSpan.FromSeconds(10)));
            return next with { Session = next.Session with { DesktopHeight = 1440 },
                Launch = next.Launch with { TargetPath = "target", RecentTargets = ["target"] } };
        }));
        try
        {
            Assert.True(building.Wait(TimeSpan.FromSeconds(10)));
            Assert.Same(before, service.Current);
            Assert.Equal(1920, before.Session.DesktopWidth);
            Assert.Empty(before.Launch.RecentTargets);
        }
        finally { publish.Set(); }
        await update.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2560, service.Current.Session.DesktopWidth);
        Assert.Equal(1440, service.Current.Session.DesktopHeight);
        Assert.Empty(before.Launch.RecentTargets);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)service.Current.Launch.RecentTargets).Add("mutation"));
        service.Flush();
        using var reloaded = new SettingsService(_directory);
        Assert.Equal("target", Assert.Single(reloaded.Current.Launch.RecentTargets));
        Assert.Equal(1440, reloaded.Current.Session.DesktopHeight);
    }

    [Fact]
    public async Task ConcurrentFlushPersistsOneCompleteSnapshot()
    {
        using var service = new SettingsService(_directory);
        service.Update(s => s with { Launch = s.Launch with { TargetPath = "0", RecentTargets = ["0"] } });
        using var start = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            for (var i = 0; i < 500; i++)
            {
                var target = (i % 4).ToString(System.Globalization.CultureInfo.InvariantCulture);
                service.Update(s => s with { Launch = s.Launch with
                {
                    TargetPath = target,
                    RecentTargets = s.Launch.RecentTargets.RemoveAll(p => p == target).Insert(0, target)
                } });
            }
        });
        start.Set();
        for (var i = 0; i < 100; i++)
        {
            service.Flush();
            var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(service.SettingsPath))!;
            Assert.Equal(saved.Launch.TargetPath, saved.Launch.RecentTargets[0]);
            Assert.InRange(saved.Launch.RecentTargets.Count, 1, 4);
            Assert.All(saved.Launch.RecentTargets, item => Assert.False(string.IsNullOrWhiteSpace(item)));
            Assert.Equal(saved.Launch.RecentTargets.Count, saved.Launch.RecentTargets.Distinct().Count());
        }
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        service.Flush();
        using var reader = new SettingsService(_directory);
        Assert.Equal(service.Current.Launch.TargetPath, reader.Current.Launch.TargetPath);
    }

    [Fact]
    public void FailedUpdateLeavesThePublishedSnapshotIntact()
    {
        using var service = new SettingsService(_directory);
        var before = service.Current;
        Assert.Throws<ArgumentNullException>(() => service.Update(null!));
        Assert.Throws<InvalidOperationException>(() => service.Update(_ => null!));
        Assert.Throws<IOException>(() => service.Update(s =>
        {
            _ = s with { ChildSessionNoticeAccepted = true };
            throw new IOException("synthetic update failure");
        }));
        Assert.Same(before, service.Current);
        service.Dispose();
        Assert.False(File.Exists(service.SettingsPath));
    }

    [Fact]
    public void PreviouslyDamagedHistoryIsRepairedAndPersisted()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), """
            { "SettingsVersion": 1, "Launch": { "RecentTargets":
              ["Target3", "Target2", "target3", null, " ", "4", "5", "6", "7", "8", "9", "10"] } }
            """);
        using (var service = new SettingsService(_directory))
            Assert.Equal(new[] { "Target3", "Target2", "4", "5", "6", "7", "8", "9" }, service.Current.Launch.RecentTargets);
        using var reader = new SettingsService(_directory);
        Assert.Equal(LaunchSettings.MaxRecentTargets, reader.Current.Launch.RecentTargets.Count);
        Assert.DoesNotContain("target3", reader.Current.Launch.RecentTargets);
        Assert.All(reader.Current.Launch.RecentTargets, item => Assert.False(string.IsNullOrWhiteSpace(item)));
    }

    [Fact]
    public void JsonNullRootUsesDefaults()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "null");
        using var service = new SettingsService(_directory);
        Assert.Equal(1920, service.Current.Session.DesktopWidth);
    }

    [Fact]
    public void 最近使用列表去重后置顶由界面负责_此处只验证可序列化()
    {
        using (var writer = new SettingsService(_directory))
        {
            writer.Update(s => s with { Launch = s.Launch with { RecentTargets = [@"C:\a.exe", @"C:\b.exe"] } });
            writer.Flush();
        }

        using var reader = new SettingsService(_directory);
        Assert.Equal(2, reader.Current.Launch.RecentTargets.Count);
        Assert.Equal(@"C:\a.exe", reader.Current.Launch.RecentTargets[0]);
    }

    [Fact]
    public void 自动登录密码表按账户分别保存且不区分大小写()
    {
        using (var writer = new SettingsService(_directory))
        {
            writer.Update(s => s with { Automation = s.Automation with { AutoLoginPasswords = s.Automation.AutoLoginPasswords.Add("batvb", "enc-1").Add("OtherUser", "enc-2") } });
            writer.Flush();
        }

        using var reader = new SettingsService(_directory);
        var passwords = reader.Current.Automation.AutoLoginPasswords;

        // 两个账户各存一份，互不覆盖
        Assert.Equal(2, passwords.Count);

        // 从文件读回来后仍然是忽略大小写的字典（反序列化会重建字典，比较器要补回来）
        Assert.True(passwords.ContainsKey("BATVB"));
        Assert.True(passwords.ContainsKey("otheruser"));
        Assert.Equal("enc-2", passwords["OTHERUSER"]);
    }

    [Fact]
    public void 提权自动启动的授权与目标程序绑定()
    {
        var path = @"C:\Games\game.exe";
        var consent = ElevatedLaunchConsent.Issue(path, "--windowed");

        Assert.NotNull(consent);
        Assert.True(ElevatedLaunchConsent.IsValid(consent, path, "--windowed"));

        // 路径大小写不影响判定
        Assert.True(ElevatedLaunchConsent.IsValid(consent, @"C:\games\GAME.EXE", "--windowed"));

        // 换程序或换参数，授权立即失效——不能靠改 settings.json 让别的程序被提权执行
        Assert.False(ElevatedLaunchConsent.IsValid(consent, @"C:\Games\other.exe", "--windowed"));
        Assert.False(ElevatedLaunchConsent.IsValid(consent, path, "--fullscreen"));
        Assert.False(ElevatedLaunchConsent.IsValid(null, path, "--windowed"));
    }

    [Fact]
    public void 旧版单账户密码会迁移到多账户表()
    {
        // 模拟 0.2.3 测试版写下的设置：只有一个 AutoLoginPassword 字段
        File.WriteAllText(
            Path.Combine(_directory, "settings.json"),
            """
            {
              "SettingsVersion": 1,
              "Automation": {
                "AutoLoginEnabled": true,
                "AutoLoginPassword": "enc-legacy"
              }
            }
            """);

        using var service = new SettingsService(_directory);
        var passwords = service.Current.Automation.AutoLoginPasswords;

        // 迁移到「当前账户」名下，值保持不变（仍是 DPAPI 密文）
        Assert.Single(passwords);
        Assert.True(passwords.TryGetValue(Environment.UserName, out var migrated));
        Assert.Equal("enc-legacy", migrated);

        // 旧字段清空，不再写回
        Assert.Null(service.Current.Automation.LegacyAutoLoginPassword);
        service.Flush();

        var json = File.ReadAllText(Path.Combine(_directory, "settings.json"));
        Assert.DoesNotContain("enc-legacy\",\n    \"AutoLoginPassword", json);
    }

    [Fact]
    public void LegacyUuSettingsAreIgnoredAndRemovedOnSave()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """
            {
              "SettingsVersion": 1,
              "Session": { "DesktopWidth": 2560 },
              "Automation": {
                "UuViewSwitchEnabled": true,
                "UuViewLockEnabled": true,
                "UuViewSwitchHotkey": "Ctrl+Alt+U",
                "AutoLoginEnabled": false,
                "AutoLoginUserName": "ExistingUser",
                "AutoLoginPasswords": { "ExistingUser": "existing-password" }
              }
            }
            """);

        using var service = new SettingsService(_directory);
        Assert.Equal(2560, service.Current.Session.DesktopWidth);
        Assert.False(service.Current.Automation.AutoLoginEnabled);
        Assert.Equal("ExistingUser", service.Current.Automation.AutoLoginUserName);
        Assert.Equal("existing-password", service.Current.Automation.AutoLoginPasswords["existinguser"]);
        service.Flush();
        Assert.DoesNotContain("UuView", File.ReadAllText(path));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响结论
        }
    }
}
