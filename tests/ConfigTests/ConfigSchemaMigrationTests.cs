using System;
using System.IO;
using System.Text.Json;
using TaskbarMonitor.Config;
using Xunit;

namespace ConfigTests;

public sealed class ConfigSchemaMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TaskbarMonitorSchemaTests_" + Guid.NewGuid().ToString("N"));

    public ConfigSchemaMigrationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void MissingSchemaVersionMigratesCompactLayoutToGridAndPersists()
    {
        string path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, "{\"layout\":\"Compact\"}");

        using (var manager = new ConfigManager(path))
        {
            Assert.Equal(ConfigData.CurrentSchemaVersion, manager.Config.ConfigSchemaVersion);
            Assert.Equal("Grid", manager.Config.Layout);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(ConfigData.CurrentSchemaVersion, document.RootElement.GetProperty("configSchemaVersion").GetInt32());
        Assert.Equal("Grid", document.RootElement.GetProperty("layout").GetString());
    }

    [Fact]
    public void CurrentSchemaVersionDoesNotMigrateAgain()
    {
        string path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, "{\"configSchemaVersion\":2,\"layout\":\"Compact\"}");

        using (var manager = new ConfigManager(path))
        {
            Assert.Equal(ConfigData.CurrentSchemaVersion, manager.Config.ConfigSchemaVersion);
            Assert.Equal("Compact", manager.Config.Layout);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("Compact", document.RootElement.GetProperty("layout").GetString());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
