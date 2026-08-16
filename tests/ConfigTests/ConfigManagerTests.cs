using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TaskbarMonitor.Config;
using Xunit;

namespace ConfigTests;

public class ConfigManagerTests : IDisposable
{
    private readonly string _tempDirectory;

    public ConfigManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "TaskbarMonitorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void DefaultConfigPathUsesPerUserLocalAppData()
    {
        string expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarMonitor",
            "Config");

        Assert.Equal(Path.Combine(expectedRoot, "config.json"), ConfigManager.GetDefaultConfigPath());
    }

    [Fact]
    public void TestDefaultConfigCreation_WhenFileDoesNotExist()
    {
        string configPath = Path.Combine(_tempDirectory, "config.json");
        Assert.False(File.Exists(configPath));

        using var manager = new ConfigManager(configPath);

        Assert.True(File.Exists(configPath));
        Assert.NotNull(manager.Config);
        Assert.True(manager.Config.Metrics.Cpu);
        Assert.True(manager.Config.Metrics.Ram);
        Assert.True(manager.Config.Metrics.Network);
        Assert.False(manager.Config.Metrics.Disk);
        Assert.False(manager.Config.Metrics.Gpu);
        Assert.True(manager.Config.Agents.CommandCode);
        Assert.True(manager.Config.Agents.OpenCode);
        Assert.False(manager.Config.Agents.Codex);
        Assert.Equal("Auto", manager.Config.Placement);
        Assert.Equal(360, manager.Config.PreferredWidth);
        Assert.False(manager.Config.Autostart);
    }

    [Fact]
    public void TestLoadSaveRoundtrip()
    {
        string configPath = Path.Combine(_tempDirectory, "config_roundtrip.json");
        using var manager = new ConfigManager(configPath);

        var modified = new ConfigData
        {
            UpdateIntervalSeconds = 5,
            PreferredWidth = 480,
            Placement = "BeforeNotificationArea",
            Autostart = true,
            Metrics = new MetricsConfig
            {
                Cpu = true,
                Ram = true,
                Network = true,
                Disk = true,
                Gpu = true
            },
            Agents = new AgentsConfig
            {
                CommandCode = true,
                OpenCode = true,
                Codex = true,
                Antigravity = true,
                Claude = true
            },
            Colors = new ColorsConfig
            {
                Cpu = "#FF0000",
                Ram = "#00FF00",
                Network = "#0000FF",
                CommandCode = "#FFFF00",
                OpenCode = "#00FFFF",
                Codex = "#FF00FF",
                Antigravity = "#FFFFFF",
                Claude = "#000000"
            }
        };

        manager.Save(modified);

        // Create new instance pointing to same file to verify disk load
        using var manager2 = new ConfigManager(configPath);

        Assert.Equal(5, manager2.Config.UpdateIntervalSeconds);
        Assert.Equal(480, manager2.Config.PreferredWidth);
        Assert.Equal("BeforeNotificationArea", manager2.Config.Placement);
        Assert.True(manager2.Config.Autostart);
        Assert.True(manager2.Config.Metrics.Disk);
        Assert.True(manager2.Config.Metrics.Gpu);
        Assert.True(manager2.Config.Agents.Claude);
        Assert.Equal("#FF0000", manager2.Config.Colors.Cpu);
    }

    [Fact]
    public async Task TestHotReloadTriggersEvent()
    {
        string configPath = Path.Combine(_tempDirectory, "config_hotreload.json");
        using var manager = new ConfigManager(configPath);

        var tcs = new TaskCompletionSource<ConfigData>();
        manager.ConfigReloaded += (s, e) =>
        {
            if (e.PreferredWidth == 520)
            {
                tcs.TrySetResult(e);
            }
        };

        // Modify file on disk directly
        string newJson = @"{
          ""preferredWidth"": 520,
          ""updateIntervalSeconds"": 2
        }";
        await File.WriteAllTextAsync(configPath, newJson, TestContext.Current.CancellationToken);

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000, TestContext.Current.CancellationToken));
        Assert.Equal(tcs.Task, completedTask);

        var result = await tcs.Task;
        Assert.Equal(520, result.PreferredWidth);
    }

    [Fact]
    public void TestBadJsonFallsBackToDefaults()
    {
        string configPath = Path.Combine(_tempDirectory, "config_bad.json");
        File.WriteAllText(configPath, "{ invalid json content !!! }");

        using var manager = new ConfigManager(configPath);

        Assert.NotNull(manager.Config);
        Assert.Equal(360, manager.Config.PreferredWidth);
        Assert.True(manager.Config.Metrics.Cpu);
    }

    [Fact]
    public void TestOldConfig_WithoutNewKeys_GetsDefaults()
    {
        // Config cũ (phase 1) không có layout/density/font keys → defaults đúng
        string configPath = Path.Combine(_tempDirectory, "config_old.json");
        File.WriteAllText(configPath, @"{
          ""metrics"": { ""cpu"": true, ""ram"": true },
          ""updateIntervalSeconds"": 1,
          ""placement"": ""Auto"",
          ""preferredWidth"": 360,
          ""autostart"": false
        }");

        using var manager = new ConfigManager(configPath);

        Assert.Equal("Compact", manager.Config.Layout);
        Assert.Equal("Compact", manager.Config.Density);
        Assert.True(manager.Config.ShowLabels);
        Assert.True(manager.Config.ShowResetCountdown);

        Assert.Equal("Sarasa Fixed SC", manager.Config.FontFamily);
        Assert.Equal(1.0, manager.Config.FontScale);
    }

    [Fact]
    public void TestNewConfig_ParsesLayoutAndFont()
    {
        string configPath = Path.Combine(_tempDirectory, "config_new.json");
        File.WriteAllText(configPath, @"{
          ""layout"": ""TwoLine"",
          ""density"": ""Comfortable"",
          ""fontFamily"": ""Sarasa Fixed SC"",
          ""fontScale"": 1.2,
          ""showLabels"": false
        }");

        using var manager = new ConfigManager(configPath);

        Assert.Equal("TwoLine", manager.Config.Layout);
        Assert.Equal("Comfortable", manager.Config.Density);
        Assert.Equal("Sarasa Fixed SC", manager.Config.FontFamily);
        Assert.Equal(1.2, manager.Config.FontScale);
        Assert.False(manager.Config.ShowLabels);

    }

    [Fact]
    public void TestNewMetricAndPositionKeys_Parse()
    {
        string configPath = Path.Combine(_tempDirectory, "config_position.json");
        File.WriteAllText(configPath, "{\"metrics\":{\"temperature\":true},\"position\":\"Right\"}");

        using var manager = new ConfigManager(configPath);

        Assert.True(manager.Config.Metrics.Temperature);
        Assert.Equal("Right", manager.Config.Position);
    }

    [Fact]
    public void LegacyPowerKeyIsIgnoredAndNotWrittenBack()
    {
        string configPath = Path.Combine(_tempDirectory, "config_legacy_power.json");
        File.WriteAllText(configPath, "{\"metrics\":{\"cpu\":true,\"power\":true}}");
        using var manager = new ConfigManager(configPath);

        manager.Save(manager.Config);

        Assert.DoesNotContain("power", File.ReadAllText(configPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveIsAtomicAndKeepsBackup()
    {
        string configPath = Path.Combine(_tempDirectory, "config_atomic.json");
        using (var manager = new ConfigManager(configPath))
        {
            manager.Save(new ConfigData { PreferredWidth = 400 });
        }

        // First save: file exists, no temp left behind, no backup yet.
        Assert.True(File.Exists(configPath));
        Assert.False(File.Exists(configPath + ".tmp"));

        using (var manager = new ConfigManager(configPath))
        {
            manager.Save(new ConfigData { PreferredWidth = 500 });
        }

        // Second save: previous good content is retained as .bak; no temp left.
        Assert.True(File.Exists(configPath));
        Assert.True(File.Exists(configPath + ".bak"));
        Assert.False(File.Exists(configPath + ".tmp"));

        // .bak holds the pre-second-save content.
        using var reader = new StreamReader(configPath + ".bak");
        string backupJson = reader.ReadToEnd();
        Assert.Contains("\"preferredWidth\": 400", backupJson);
    }

    [Fact]
    public void CorruptActiveFileRecoversFromBackup()
    {
        string configPath = Path.Combine(_tempDirectory, "config_backup_recovery.json");
        using (var manager = new ConfigManager(configPath))
        {
            manager.Save(new ConfigData { PreferredWidth = 440 });
            manager.Save(new ConfigData { PreferredWidth = 480 });
        }

        // Corrupt the active file; the .bak from the previous save is intact.
        File.WriteAllText(configPath, "{ broken json !!!");

        using var manager2 = new ConfigManager(configPath);

        Assert.Equal(440, manager2.Config.PreferredWidth);
    }
}
