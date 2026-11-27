using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using DesktopTwin.Models;

namespace DesktopTwin.Services;

/// <summary>
/// 设置读写服务。
///
/// 写盘采用「延迟合并 + 显式冲刷」策略：界面上的每次开关都会调用 <see cref="Update"/>，
/// 800 毫秒内的连续改动只写一次盘；明确需要立即持久化时调用 <see cref="Flush"/>。
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _syncRoot = new();
    private readonly object _writeLock = new();
    private volatile AppSettings _current;
    private readonly Timer _saveTimer;
    private AppSettings? _persisted;
    private bool _disposed;
    private bool _migrated;

    /// <summary>
    /// 创建设置服务并立即加载已有配置。
    /// </summary>
    /// <param name="settingsDirectory">
    /// 设置目录。默认 <c>%AppData%\DesktopTwin</c>；传入其他路径主要用于测试。
    /// </param>
    public SettingsService(string? settingsDirectory = null)
    {
        SettingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin");
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
        _saveTimer = new Timer(OnSaveTimerElapsed, state: null, Timeout.Infinite, Timeout.Infinite);
        _current = Load();
        _persisted = _migrated ? null : _current;

        // 迁移过就立刻写回，否则每次启动都要再迁一遍，而且用户看到的是被临时改过的值。
        if (_migrated)
        {
            ScheduleSave();
        }
    }

    /// <summary>
    /// 当前生效的不可变快照。持有旧快照的读者不会看到半次修改。
    /// </summary>
    public AppSettings Current => _current;

    /// <summary>
    /// 设置文件所在目录。
    /// </summary>
    public string SettingsDirectory { get; }

    /// <summary>
    /// 设置文件完整路径。
    /// </summary>
    public string SettingsPath { get; }

    /// <summary>
    /// 原子发布一次完整修改，稍后合并写盘。回调不得执行 I/O 或等待外部操作。
    /// 释放后忽略迟到的修改，避免退出时后台诊断通知重新安排写盘。
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_syncRoot)
        {
            if (_disposed) return;
            var next = change(_current) ?? throw new InvalidOperationException("A settings update must return a snapshot.");
            if (ReferenceEquals(next, _current)) return;
            _current = next;
            ScheduleSave();
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 立即写盘；失败向调用方传播，未持久化的快照保留给下次修改或退出时补存。
    /// </summary>
    public void Flush()
    {
        lock (_writeLock)
        {
            AppSettings snapshot;
            lock (_syncRoot)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                snapshot = _current;
            }
            WriteToDisk(snapshot);
        }
    }

    /// <summary>
    /// 停止计时器并做最后一次写盘。
    /// </summary>
    public void Dispose()
    {
        lock (_writeLock)
        {
            AppSettings? snapshot;
            lock (_syncRoot)
            {
                if (_disposed) return;
                _disposed = true;
                snapshot = ReferenceEquals(_persisted, _current) ? null : _current;
                _saveTimer.Dispose();
            }
            if (snapshot is not null) TryWriteToDisk(snapshot);
        }
    }

    private void OnSaveTimerElapsed(object? state)
    {
        lock (_writeLock)
        {
            AppSettings snapshot;
            lock (_syncRoot)
            {
                if (_disposed || ReferenceEquals(_persisted, _current)) return;
                snapshot = _current;
            }
            TryWriteToDisk(snapshot);
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings { SettingsVersion = AppSettings.CurrentVersion };
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                ?? new AppSettings { SettingsVersion = AppSettings.CurrentVersion };

            _migrated = AppSettingsNormalizer.NormalizeAndMigrate(ref settings, Environment.UserName);
            return settings;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            AppLog.Warn($"读取设置失败，已回退到默认设置：{SettingsPath}", exception);
            return new AppSettings { SettingsVersion = AppSettings.CurrentVersion };
        }
    }

    private void TryWriteToDisk(AppSettings snapshot)
    {
        try
        {
            WriteToDisk(snapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"保存设置失败：{SettingsPath}", exception);
        }
    }

    private void WriteToDisk(AppSettings snapshot)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, SerializerOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
        // Readers may have published a newer snapshot during I/O. Save only this
        // immutable identity; the newer snapshot must still be considered dirty.
        lock (_syncRoot) _persisted = snapshot;
    }
}
