using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarMonitor.AgentUsage;
using TaskbarMonitor.Config;
using TaskbarMonitor.Metrics;
using TaskbarMonitor.UI.Layout;

namespace TaskbarMonitor.UI;

/// <summary>
/// ViewModel chính cho widget taskbar.
/// - LayoutManager build pod list theo layout (1 hàng hoặc 2 hàng TwoLine).
/// - Tick() chỉ update text/image của pod đã có — không rebuild UI tree.
/// - Wire trực tiếp MetricSampler + UsagePoller (không reflection).
/// - Chỉ cập nhật text metric/usage; không render chart trong taskbar.
/// </summary>
public class TaskbarContentViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly LayoutManager _layout = new();
    private readonly List<IDisposable> _disposables = new();

    private MetricSampler? _sampler;
    private NetworkMonitor? _network;
    private TemperatureMonitor? _temperature;
    private UsagePoller? _poller;
    private string _fontFamily = "Sarasa Fixed SC";
    private string _networkRateText = "↑ -- ↓ --";
    private bool _disposed;

    public ConfigData Config => ConfigManager.Instance.Config;

    public ObservableCollection<PodViewModel> Pods => _layout.Pods;
    public IReadOnlyList<IReadOnlyList<PodViewModel>> Rows => _layout.Rows;
    public bool IsGrid => _layout.CurrentLayout.Name == "Grid";
    public bool IsTwoRow => _layout.IsTwoRow && !IsGrid;
    public bool IsSingleRow => !IsTwoRow && !IsGrid;
    public IReadOnlyList<PodViewModel> GridMetrics => Rows.Count > 0 ? Rows[0] : Array.Empty<PodViewModel>();
    public PodViewModel? GridCpu => GridMetrics.FirstOrDefault(p => p.Key == "cpu");
    public PodViewModel? GridRam => GridMetrics.FirstOrDefault(p => p.Key == "ram");
    public PodViewModel? GridGpu => GridMetrics.FirstOrDefault(p => p.Key == "gpu");
    public PodViewModel? GridDisk => GridMetrics.FirstOrDefault(p => p.Key == "disk");
    public PodViewModel? GridNetwork => GridMetrics.FirstOrDefault(p => p.Key == "network");
    public AgentPodViewModel? GridCodex => GridAgents.OfType<AgentPodViewModel>().FirstOrDefault(p => p.Key == "Codex");
    public AgentPodViewModel? GridProvider => GridAgents.OfType<AgentPodViewModel>().FirstOrDefault(p => p.Key != "Codex");
    public IReadOnlyList<PodViewModel> GridAgents => Rows.Count > 1 ? Rows[1] : Array.Empty<PodViewModel>();
    public AgentPodViewModel? GridPrimaryAgent => GridAgents.OfType<AgentPodViewModel>().FirstOrDefault();
    public IReadOnlyList<AgentPodViewModel> GridSecondaryAgents => GridAgents.OfType<AgentPodViewModel>().Skip(1).ToList();

    /// <summary>Raise khi layout thay đổi (pods rebuild) — MainWindow dùng để auto-resize widget.</summary>
    public event EventHandler? LayoutChanged;

    public string FontFamily
    {
        get => _fontFamily;
        set
        {
            if (SetField(ref _fontFamily, value))
            {
                OnPropertyChanged(nameof(FontFamily));
            }
        }
    }

    public string NetworkRateText
    {
        get => _networkRateText;
        private set => SetField(ref _networkRateText, value);
    }

    public TaskbarContentViewModel()
    {
        InitMetricsSampler();
        InitUsagePoller();

        ConfigManager.Instance.ConfigReloaded += OnConfigReloaded;
        _layout.LayoutChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Pods));
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(IsTwoRow));
            OnPropertyChanged(nameof(IsGrid));
            OnPropertyChanged(nameof(IsSingleRow));
            OnPropertyChanged(nameof(GridMetrics));
            OnPropertyChanged(nameof(GridCpu));
            OnPropertyChanged(nameof(GridRam));
            OnPropertyChanged(nameof(GridGpu));
            OnPropertyChanged(nameof(GridDisk));
            OnPropertyChanged(nameof(GridNetwork));
            OnPropertyChanged(nameof(GridCodex));
            OnPropertyChanged(nameof(GridProvider));
            OnPropertyChanged(nameof(GridAgents));
            OnPropertyChanged(nameof(GridPrimaryAgent));
            OnPropertyChanged(nameof(GridSecondaryAgents));
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, Config.UpdateIntervalSeconds))
        };
        _timer.Tick += (_, _) => Tick();

        ApplyConfigSettings(); // cần _timer đã tồn tại (đặt interval theo layout)

        _timer.Start();
        Tick();
    }

    private void InitMetricsSampler()
    {
        try
        {
            var readers = new Dictionary<string, Func<double>>();

            try
            {
                var cpu = new CpuMonitor();
                _disposables.Add(cpu);
                readers["cpu"] = () => cpu.Sample().UsagePercent;
            }
            catch { readers["cpu"] = () => double.NaN; }

            try
            {
                var mem = new MemoryMonitor();
                _disposables.Add(mem);
                // MemoryMetrics.Usage is a fraction (0..1), while the UI and
                // chart use percentages (0..100). Keeping the conversion at
                // the sampling boundary avoids the old "RAM 0%" display.
                readers["ram"] = () => mem.Sample().Usage * 100.0;
            }
            catch { readers["ram"] = () => double.NaN; }

            try
            {
                    _network = new NetworkMonitor();
                readers["netUp"] = () => _network.SampleCached().SentBytesPerSecond / 1024.0;
                readers["netDown"] = () => _network.SampleCached().ReceivedBytesPerSecond / 1024.0;
            }
            catch
            {
                readers["netUp"] = () => double.NaN;
                readers["netDown"] = () => double.NaN;
            }

            try
            {
                var disk = new DiskMonitor();
                _disposables.Add(disk);
                var diskReader = new ThrottledMetricReader(
                    () =>
                    {
                        var samples = disk.Sample();
                        return samples.Count > 0 ? samples[0].UsagePercent : double.NaN;
                    },
                    TimeSpan.FromSeconds(10));
                readers["disk"] = diskReader.Read;
            }
            catch { readers["disk"] = () => double.NaN; }

            try
            {
                    var gpu = new GpuMonitor();
                _disposables.Add(gpu);
                var gpuReader = new ThrottledMetricReader(gpu.Sample, TimeSpan.FromSeconds(5));
                readers["gpu"] = gpuReader.Read;
            }
            catch { readers["gpu"] = () => double.NaN; }

            try
            {
                _temperature = new TemperatureMonitor();
                _disposables.Add(_temperature);
                var temperatureReader = new ThrottledMetricReader(_temperature.SampleCelsius, TimeSpan.FromSeconds(10));
                readers["temperature"] = temperatureReader.Read;
            }
            catch { readers["temperature"] = () => double.NaN; }

            // GPU thermal libraries often need privileged driver access. This
            // user-mode widget intentionally does not load hardware drivers;
            // lack of a trusted sensor is shown as --°C.
            readers["gpuTemperature"] = () => double.NaN;

            // Native counters and adapter enumeration are already cached by the
            // UI; a two-second sampling cadence keeps the widget responsive
            // while avoiding a permanent 1 Hz native-counter wake-up.
            _sampler = new MetricSampler(readers, 2000, 60);
            _sampler.Start();
        }
        catch
        {
            _sampler = null; // fallback random nếu không khởi tạo được
        }
    }

    private void InitUsagePoller()
    {
        try
        {
            var options = new UsagePollerOptions
            {
                CommandCodeEnabled = Config.Agents.CommandCode,
                OpenCodeEnabled = Config.Agents.OpenCode,
                CodexEnabled = Config.Agents.Codex,
                AntigravityEnabled = Config.Agents.Antigravity,
                ClaudeEnabled = Config.Agents.Claude
            };
            _poller = new UsagePoller(options);
            _disposables.Add(_poller);
            _poller.Start();
        }
        catch
        {
            _poller = null;
        }
    }

    private void OnConfigReloaded(object? sender, ConfigData newConfig)
    {
        App.Current?.Dispatcher.InvokeAsync(() =>
        {
            ApplyConfigSettings(forceRebuild: true); // config reload → ép rebuild pods
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, newConfig.UpdateIntervalSeconds));
            OnPropertyChanged(string.Empty);
            Tick();
        });
    }

    private void ApplyConfigSettings(bool forceRebuild = false)
    {
        FontFamily = Config.FontFamily;
        var kind = ParseLayoutKind(Config.Layout);
        _layout.Apply(kind, Config, forceRebuild);

        // Performance: layout Minimal → tick 2s (ít CPU wake) — nếu config interval nhỏ hơn
        if (kind == WidgetLayoutKind.Minimal)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(2, Config.UpdateIntervalSeconds));
        }
        else
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, Config.UpdateIntervalSeconds));
        }
    }

    private static WidgetLayoutKind ParseLayoutKind(string? layout)
    {
        if (Enum.TryParse<WidgetLayoutKind>(layout, ignoreCase: true, out var kind))
        {
            return kind;
        }
        return WidgetLayoutKind.Compact;
    }

    private void Tick()
    {
        UpdateMetrics();
        UpdateAgents();
    }

    private void UpdateMetrics()
    {
        var metricPods = Pods.OfType<MetricPodViewModel>().ToList();
        if (metricPods.Count == 0) return;

        float cpu = ReadMetric("cpu");
        float ram = ReadMetric("ram");
        float netUp = ReadMetric("netUp");
        float netDown = ReadMetric("netDown");
        float disk = ReadMetric("disk");
        float gpu = ReadMetric("gpu");
        float cpuTemperature = Config.Metrics.Temperature ? ReadMetric("temperature") : float.NaN;
        float gpuTemperature = Config.Metrics.Temperature ? ReadMetric("gpuTemperature") : float.NaN;
        NetworkRateText = NetworkRateTextFormatter.FormatPair(netUp, netDown);

        foreach (var pod in metricPods)
        {
            float value;
            string text;
            switch (pod.Key)
            {
                case "cpu":
                    value = cpu;
                    text = float.IsFinite(value) && cpuTemperature > 0
                        ? $"CPU {value:F0}% · {cpuTemperature:F0}°C"
                        : float.IsFinite(value) ? $"CPU {value:F0}% · --°C" : "CPU -- · --°C";
                    break;
                case "ram":
                    value = ram;
                    text = float.IsFinite(value) ? $"RAM {value:F0}%" : "RAM --";
                    break;
                case "network":
                    value = float.IsFinite(netUp) && float.IsFinite(netDown)
                        ? Math.Min(100f, (netUp + netDown) / 10f) : 0;
                    text = FormatNetworkText(netUp, netDown);
                    break;
                case "disk":
                    value = disk;
                    text = float.IsFinite(disk) ? $"DISK {disk:F0}%" : "DISK --";
                    break;
                case "gpu":
                    value = gpu;
                    text = float.IsFinite(gpu) && gpuTemperature > 0
                        ? $"GPU {gpu:F0}% · {gpuTemperature:F0}°C"
                        : float.IsFinite(gpu) ? $"GPU {gpu:F0}% · --°C" : "GPU -- · --°C";
                    break;
                default:
                    value = 0;
                    text = pod.Label;
                    break;
            }

            pod.ValueText = text;

        }
    }

    private void UpdateAgents()
    {
        var agentPods = Pods.OfType<AgentPodViewModel>().ToList();
        if (agentPods.Count == 0) return;

        bool showReset = Config.ShowResetCountdown && _layout.CurrentLayout.ShowAgentResetText;
        bool showLabels = Config.ShowLabels;
        foreach (var pod in agentPods)
        {
            var data = _poller?.Get(ToAgentId(pod.Key));

            double? pct5h = null, pct7d = null;
            DateTime? reset5h = null, reset7d = null;
            string valueText = "--";

            if (data != null)
            {
                pct5h = data.UsedPercent5h;
                pct7d = data.UsedPercent7d;
                reset5h = data.ResetsAt5h?.LocalDateTime;
                reset7d = data.ResetsAt7d?.LocalDateTime;

                var last5h = data.Last5h;
                if (last5h != null && data.UsedPercent5h == null)
                {
                    // SQLite agents: hiển thị cost + tokens thay vì % quota
                    double cost = last5h.Cost ?? 0;
                    double tok = (last5h.TokensTotal ?? 0) / 1e6;
                    valueText = $"${cost:F2} · {tok:F1}M";
                }
                else
                {
                    valueText = UsageTextFormatter.FormatQuotaPercent(pct5h, showReset, reset5h);
                }
            }

            pod.ValueText = valueText;
            pod.FiveHourPercentage = pct5h ?? double.NaN;
            pod.SevenDayPercentage = pct7d ?? double.NaN;
            pod.FiveHourResetText = UsageTextFormatter.FormatResetTime(reset5h);
            pod.SevenDayResetText = UsageTextFormatter.FormatResetTime(reset7d);


        }
    }


    private static string ToAgentId(string key) => key switch
    {
        "CommandCode" => AgentIds.CommandCode,
        "OpenCode" => AgentIds.OpenCode,
        "Codex" => AgentIds.Codex,
        "Antigravity" => AgentIds.Antigravity,
        "Claude" => AgentIds.Claude,
        _ => key.ToLowerInvariant()
    };


    private float ReadMetric(string key)
    {
        if (_sampler != null)
        {
            try
            {
                var hist = _sampler.GetHistory(key);
                if (hist.Count > 0) return (float)hist[^1];
            }
            catch { }
        }
        return float.NaN;
    }



    private static string FormatNetworkText(float upKbps, float downKbps)
    {
        if (!float.IsFinite(upKbps) || !float.IsFinite(downKbps)) return "↑ --  ↓ --";
        string upText = upKbps > 1024 ? $"{upKbps / 1024f:F1} MB/s" : $"{upKbps:F0} KB/s";
        string downText = downKbps > 1024 ? $"{downKbps / 1024f:F1} MB/s" : $"{downKbps:F0} KB/s";
        return $"↑ {upText} ↓ {downText}";
    }


    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        ConfigManager.Instance.ConfigReloaded -= OnConfigReloaded;
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
    }
}
