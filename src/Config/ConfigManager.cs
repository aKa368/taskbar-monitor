using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace TaskbarMonitor.Config;

public class MetricsConfig
{
    [JsonPropertyName("cpu")]
    public bool Cpu { get; set; } = true;

    [JsonPropertyName("ram")]
    public bool Ram { get; set; } = true;

    [JsonPropertyName("network")]
    public bool Network { get; set; } = true;

    [JsonPropertyName("disk")]
    public bool Disk { get; set; } = false;

    [JsonPropertyName("gpu")]
    public bool Gpu { get; set; } = false;

    [JsonPropertyName("temperature")]
    public bool Temperature { get; set; } = true;

    [JsonPropertyName("ramTemperature")]
    public bool RamTemperature { get; set; } = false;
}

public class AgentsConfig
{
    [JsonPropertyName("commandcode")]
    public bool CommandCode { get; set; } = true;

    [JsonPropertyName("opencode")]
    public bool OpenCode { get; set; } = true;

    [JsonPropertyName("codex")]
    public bool Codex { get; set; } = false;

    [JsonPropertyName("antigravity")]
    public bool Antigravity { get; set; } = false;

    [JsonPropertyName("claude")]
    public bool Claude { get; set; } = false;
}

public class ColorsConfig
{
    [JsonPropertyName("cpu")]
    public string Cpu { get; set; } = "#007ACC";

    [JsonPropertyName("ram")]
    public string Ram { get; set; } = "#28A745";

    [JsonPropertyName("network")]
    public string Network { get; set; } = "#17A2B8";

    [JsonPropertyName("commandcode")]
    public string CommandCode { get; set; } = "#6F42C1";

    [JsonPropertyName("opencode")]
    public string OpenCode { get; set; } = "#FD7E14";

    [JsonPropertyName("codex")]
    public string Codex { get; set; } = "#10A37F";

    [JsonPropertyName("antigravity")]
    public string Antigravity { get; set; } = "#4285F4";

    [JsonPropertyName("claude")]
    public string Claude { get; set; } = "#D97706";
}

public class ConfigData
{
    [JsonPropertyName("metrics")]
    public MetricsConfig Metrics { get; set; } = new();

    [JsonPropertyName("agents")]
    public AgentsConfig Agents { get; set; } = new();

    [JsonPropertyName("colors")]
    public ColorsConfig Colors { get; set; } = new();

    [JsonPropertyName("updateIntervalSeconds")]
    public int UpdateIntervalSeconds { get; set; } = 1;

    [JsonPropertyName("placement")]
    public string Placement { get; set; } = "Auto";

    /// <summary>Friendly taskbar position: Left, Center, or Right.</summary>
    [JsonPropertyName("position")]
    public string Position { get; set; } = "Center";

    [JsonPropertyName("preferredWidth")]
    public double PreferredWidth { get; set; } = 360;

    [JsonPropertyName("layout")]
    public string Layout { get; set; } = "Compact";

    [JsonPropertyName("density")]
    public string Density { get; set; } = "Compact";

    [JsonPropertyName("showLabels")]
    public bool ShowLabels { get; set; } = true;

    [JsonPropertyName("showResetCountdown")]
    public bool ShowResetCountdown { get; set; } = true;


    [JsonPropertyName("fontFamily")]
    public string FontFamily { get; set; } = "Sarasa Fixed SC";

    [JsonPropertyName("fontScale")]
    public double FontScale { get; set; } = 1.0;

    [JsonPropertyName("autostart")]
    public bool Autostart { get; set; } = false;
}

public class ConfigManager : INotifyPropertyChanged, IDisposable
{
    private static readonly Lazy<ConfigManager> _instance = new(() => new ConfigManager());
    public static ConfigManager Instance => _instance.Value;

    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private ConfigData _config = new();
    private string _configPath;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ConfigData>? ConfigReloaded;

    public string ConfigPath => _configPath;

    public ConfigData Config
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
        private set
        {
            lock (_lock)
            {
                _config = value;
            }
            OnPropertyChanged();
            ConfigReloaded?.Invoke(this, value);
        }
    }

    public ConfigManager(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
        Load();
        StartWatcher();
    }

    public static string GetDefaultConfigPath()
    {
        // Installed applications cannot safely write beside their executable in
        // Program Files. Keep per-user settings in LocalAppData instead.
        string configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarMonitor",
            "Config");
        return Path.Combine(configDir, "config.json");
    }

    public static ConfigData GetDefaultConfig()
    {
        return new ConfigData();
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    var defaultConfig = GetDefaultConfig();
                    SaveInternal(defaultConfig, _configPath);
                    _config = defaultConfig;
                    return;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                var loaded = DeserializeOrNull(options);
                if (loaded != null)
                {
                    // Ensure sub-objects are not null
                    loaded.Metrics ??= new MetricsConfig();
                    loaded.Agents ??= new AgentsConfig();
                    loaded.Colors ??= new ColorsConfig();
                    _config = loaded;
                }
                else
                {
                    // The active file was corrupt; prefer the last good backup
                    // over a hard reset to defaults.
                    _config = TryLoadBackup(options) ?? GetDefaultConfig();
                }
            }
            catch (Exception)
            {
                // Fall back to default config if file is corrupt or unreadable
                _config = GetDefaultConfig();
            }
        }

        OnPropertyChanged(nameof(Config));
        ConfigReloaded?.Invoke(this, Config);
    }

    private ConfigData? DeserializeOrNull(JsonSerializerOptions options)
    {
        string json = ReadFileWithRetry(_configPath);
        try
        {
            return JsonSerializer.Deserialize<ConfigData>(json, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ConfigData? TryLoadBackup(JsonSerializerOptions options)
    {
        string backupPath = _configPath + ".bak";
        if (!File.Exists(backupPath))
            return null;
        try
        {
            string json = ReadFileWithRetry(backupPath);
            var loaded = JsonSerializer.Deserialize<ConfigData>(json, options);
            if (loaded == null)
                return null;
            loaded.Metrics ??= new MetricsConfig();
            loaded.Agents ??= new AgentsConfig();
            loaded.Colors ??= new ColorsConfig();
            return loaded;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(ConfigData? configToSave = null)
    {
        lock (_lock)
        {
            var target = configToSave ?? _config;
            SaveInternal(target, _configPath);
            _config = target;
        }

        OnPropertyChanged(nameof(Config));
    }

    private static void SaveInternal(ConfigData data, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        string json = JsonSerializer.Serialize(data, options);

        // Atomic write: serialize to a temp file in the same directory, then
        // replace the target. A crash mid-write can never leave a truncated
        // config.json, and the previous good content is kept as .bak.
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, path + ".bak");
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            // Last-resort fallback if the volume does not support atomic
            // replace (rare): copy in place and clean up the temp file.
            File.Copy(tempPath, path, overwrite: true);
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static string ReadFileWithRetry(string path, int maxAttempts = 3)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (i < maxAttempts - 1)
            {
                Thread.Sleep(50);
            }
        }
        return File.ReadAllText(path);
    }

    private void StartWatcher()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_configPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return;
            }

            string fileName = Path.GetFileName(_configPath);

            _watcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnConfigFileChanged;
            _watcher.Created += OnConfigFileChanged;
            _watcher.Renamed += OnConfigFileChanged;
        }
        catch (Exception)
        {
            // Ignore watcher failure if path inaccessible
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce hot-reload events
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ =>
        {
            Load();
        }, null, 200, Timeout.Infinite);
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            _debounceTimer?.Dispose();
        }
    }
}
