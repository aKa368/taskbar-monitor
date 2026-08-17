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
/// ViewModel chÃ­nh cho widget taskbar.
/// - LayoutManager build pod list theo layout (1 hÃ ng hoáº·c 2 hÃ ng TwoLine).
/// - Tick() chá»‰ update text/image cá»§a pod Ä‘Ã£ cÃ³ â€” khÃ´ng rebuild UI tree.
/// - Wire trá»±c tiáº¿p MetricSampler + UsagePoller (khÃ´ng reflection).
/// - Chá»‰ cáº­p nháº­t text metric/usage; khÃ´ng render chart trong taskbar.
/// </summary>
public class TaskbarContentViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SharedTaskbarTickSource _timer;
    private readonly LayoutManager _layout = new();
    private readonly List<IDisposable> _metricDisposables = new();

    private MetricSampler? _sampler;
    private NetworkMonitor? _network;
    private TemperatureMonitor? _temperature;
    private string _ramTemperatureReason = "DIMM temperature unavailable";
    private string _gpuTemperatureSource = "GPU driver exposes no temperature telemetry";
    private string? _metricSignature;
    private UsagePoller? _poller;
    private string? _usageInitializationError;
    private string _fontFamily = "Sarasa Fixed SC";
    private string _networkRateText = "UP -- DOWN --";
    private string _gridPerformanceText = string.Empty;
    private bool _disposed;

    public ConfigData Config => ConfigManager.Instance.Config;

    public ObservableCollection<PodViewModel> Pods => _layout.Pods;
    public IReadOnlyList<AgentPodViewModel> AccountPods => Pods.OfType<AgentPodViewModel>().ToList();
    public IReadOnlyList<MetricPodViewModel> SystemPods => Pods.OfType<MetricPodViewModel>().ToList();
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

    /// <summary>Raise khi layout thay Ä‘á»•i (pods rebuild) â€” MainWindow dÃ¹ng Ä‘á»ƒ auto-resize widget.</summary>
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

    public string GridPerformanceText
    {
        get => _gridPerformanceText;
        private set => SetField(ref _gridPerformanceText, value);
    }

    public bool HasGridPerformance => Config.Metrics.Gpu;

    public string GridPerformanceToolTip
    {
        get
        {
            return $"GPU utilization â€” busiest physical engine  -  Temperature: {_gpuTemperatureSource}";
        }
    }

    public TaskbarContentViewModel()
    {
        InitMetricsSampler();
        InitUsagePoller();

        ConfigManager.Instance.ConfigReloaded += OnConfigReloaded;
        _layout.LayoutChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Pods));
            OnPropertyChanged(nameof(AccountPods));
            OnPropertyChanged(nameof(SystemPods));
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
            OnPropertyChanged(nameof(HasGridPerformance));
            OnPropertyChanged(nameof(GridPerformanceText));
            OnPropertyChanged(nameof(GridPerformanceToolTip));
            OnPropertyChanged(nameof(GridCodex));
            OnPropertyChanged(nameof(GridProvider));
            OnPropertyChanged(nameof(GridAgents));
            OnPropertyChanged(nameof(GridPrimaryAgent));
            OnPropertyChanged(nameof(GridSecondaryAgents));
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };

        _timer = SharedTaskbarTickSource.Subscribe(
            Tick,
            TimeSpan.FromSeconds(Math.Max(1, Config.UpdateIntervalSeconds)));

        ApplyConfigSettings(); // cáº§n _timer Ä‘Ã£ tá»“n táº¡i (Ä‘áº·t interval theo layout)

        _timer.Start();
        Tick();
    }

    private void InitMetricsSampler()
    {
        string signature = $"{Config.Metrics.Cpu}:{Config.Metrics.Ram}:{Config.Metrics.Network}:{Config.Metrics.Disk}:{Config.Metrics.Gpu}:{Config.Metrics.Temperature}:{Config.Metrics.RamTemperature}";
        if (signature == _metricSignature) return;
        _metricSignature = signature;
        _sampler?.Dispose();
        _sampler = null;
        foreach (var disposable in _metricDisposables) disposable.Dispose();
        _metricDisposables.Clear();
        _network = null;
        _temperature = null;
        try
        {
            var readers = new Dictionary<string, Func<double>>();
            CpuMonitor? cpuMonitor = null;

            if (Config.Metrics.Cpu) try
                {
                    var cpu = new CpuMonitor();
                    cpuMonitor = cpu;
                    _metricDisposables.Add(cpu);
                    readers["cpu"] = () => cpu.Sample().UsagePercent;
                }

                catch { readers["cpu"] = () => double.NaN; }

            if (Config.Metrics.Ram) try
                {
                    var mem = new MemoryMonitor();
                    _metricDisposables.Add(mem);
                    // MemoryMetrics.Usage is a fraction (0..1), while the UI and
                    // chart use percentages (0..100). Keeping the conversion at
                    // the sampling boundary avoids the old "RAM 0%" display.
                    readers["ram"] = () => mem.Sample().Usage * 100.0;
                }
                catch { readers["ram"] = () => double.NaN; }

            if (Config.Metrics.Network) try
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

            if (Config.Metrics.Disk) try
                {
                    var disk = new DiskMonitor();
                    _metricDisposables.Add(disk);
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

            if (Config.Metrics.Gpu) try
                {
                    var gpu = new GpuMonitor();
                    _metricDisposables.Add(gpu);
                    var gpuReader = new ThrottledMetricReader(gpu.Sample, TimeSpan.FromSeconds(5));
                    readers["gpu"] = gpuReader.Read;
                }
                catch { readers["gpu"] = () => double.NaN; }

            if (Config.Metrics.Gpu && Config.Metrics.Temperature) try
                {
                    var gpuTemperature = new GpuTemperatureMonitor();
                    _metricDisposables.Add(gpuTemperature);
                    var gpuTemperatureReader = new ThrottledMetricReader(() =>
                    {
                        double value = gpuTemperature.SampleCelsius();
                        _gpuTemperatureSource = gpuTemperature.SourceDescription;
                        return value;
                    }, TimeSpan.FromSeconds(8));
                    readers["gpuTemperature"] = gpuTemperatureReader.Read;
                }
                catch { readers["gpuTemperature"] = () => double.NaN; }

            if (Config.Metrics.Temperature && Config.Metrics.Cpu) try
                {
                    _temperature = new TemperatureMonitor();
                    _metricDisposables.Add(_temperature);
                    var temperatureReader = new ThrottledMetricReader(
                        () => _temperature.SampleCelsius(cpuMonitor?.LastUsagePercent ?? double.NaN),
                        TimeSpan.FromSeconds(10));

                    readers["temperature"] = temperatureReader.Read;
                }
                catch { readers["temperature"] = () => double.NaN; }

            if (Config.Metrics.Ram && Config.Metrics.Temperature && Config.Metrics.RamTemperature)
            {
                var ramTemperature = new RamTemperatureMonitor();
                _metricDisposables.Add(ramTemperature);
                readers["ramTemperature"] = () =>
                {
                    RamTemperatureReading reading = ramTemperature.Sample();
                    _ramTemperatureReason = reading.Reason;
                    return reading.Celsius;
                };
            }

            // GPU thermal libraries often need privileged driver access. This
            // user-mode widget intentionally does not load hardware drivers;
            // lack of a trusted sensor is shown as -- C.

            // Native counters and adapter enumeration are already cached by the
            // UI; a 2.5-second sampling cadence keeps the widget responsive
            // while avoiding unnecessary native-counter wake-ups.
            if (readers.Count > 0)
            {
                _sampler = new MetricSampler(readers, 2500, 2, useSharedClock: true);
                // External GPU telemetry may take up to its process timeout.
                // Prime lightweight counters synchronously; the timer performs
                // the first GPU-temperature read on its worker thread.
                _sampler.SampleNow(static key => key != "gpuTemperature");
                _sampler.Start();
            }
        }
        catch
        {
            _sampler = null; // fallback random náº¿u khÃ´ng khá»Ÿi táº¡o Ä‘Æ°á»£c
        }
    }

    private void InitUsagePoller()
    {
        UsagePoller? poller = null;
        try
        {
            var options = CreatePollerOptions(Config);
            poller = new UsagePoller(options);
            _poller = poller;
            try
            {
                Task initial = poller.StartAsync();
                _ = ObserveUsageStartupAsync(poller, initial);
            }
            catch (Exception ex)
            {
                _usageInitializationError = $"Usage polling could not start ({ex.GetType().Name}).";
                _ = ObserveUsageStartupAsync(poller, Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            _poller = null;
            poller?.Dispose();
            _usageInitializationError = $"Usage polling could not initialize ({ex.GetType().Name}).";
        }
    }

    private async Task ObserveUsageStartupAsync(UsagePoller poller, Task initial)
    {
        try
        {
            await initial.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (Config.Agents.Codex && poller.Get(AgentIds.Codex) is null)
                await poller.QueueApiRefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _usageInitializationError = $"ChatGPT usage refresh did not complete ({ex.GetType().Name}).";
        }

        if (Config.Agents.Codex && poller.Get(AgentIds.Codex) is null)
            _usageInitializationError ??= "ChatGPT usage refresh did not produce a result.";
        App.Current?.Dispatcher.BeginInvoke(() => Tick());
    }

    private void OnConfigReloaded(object? sender, ConfigData newConfig)
    {
        App.Current?.Dispatcher.InvokeAsync(() =>
        {
            InitMetricsSampler();
            _poller?.Reconfigure(CreatePollerOptions(newConfig));
            ApplyConfigSettings(forceRebuild: true); // config reload â†’ Ã©p rebuild pods
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, newConfig.UpdateIntervalSeconds));
            OnPropertyChanged(string.Empty);
            Tick();
        });
    }

    private static UsagePollerOptions CreatePollerOptions(ConfigData config) => new()
    {
        CommandCodeEnabled = config.Agents.CommandCode,
        OpenCodeEnabled = config.Agents.OpenCode,
        CodexEnabled = config.Agents.Codex,
        AntigravityEnabled = config.Agents.Antigravity,
        ClaudeEnabled = config.Agents.Claude
    };

    private void ApplyConfigSettings(bool forceRebuild = false)
    {
        FontFamily = Config.FontFamily;
        var kind = ParseLayoutKind(Config.Layout);
        _layout.Apply(kind, Config, forceRebuild);

        // Performance: layout Minimal â†’ tick 2s (Ã­t CPU wake) â€” náº¿u config interval nhá» hÆ¡n
        if (kind == WidgetLayoutKind.Minimal)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(2, Config.UpdateIntervalSeconds));
        }
        else
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, Config.UpdateIntervalSeconds));
        }
    }

    public static WidgetLayoutKind ParseLayoutKind(string? layout)
    {
        if (Enum.TryParse<WidgetLayoutKind>(layout, ignoreCase: true, out var kind))
        {
            return WidgetLayoutKind.Grid;
        }
        return WidgetLayoutKind.Grid;
    }

    private void Tick()
    {
        UpdateMetrics();
        UpdateAgents();
    }

    private void UpdateMetrics()
    {
        var metricPods = Pods.OfType<MetricPodViewModel>();
        if (!metricPods.Any()) return;

        float cpu = Config.Metrics.Cpu ? ReadMetric("cpu") : float.NaN;
        float ram = Config.Metrics.Ram ? ReadMetric("ram") : float.NaN;
        float ramTemperature = Config.Metrics.RamTemperature ? ReadMetric("ramTemperature") : float.NaN;
        float netUp = Config.Metrics.Network ? ReadMetric("netUp") : float.NaN;
        float netDown = Config.Metrics.Network ? ReadMetric("netDown") : float.NaN;
        float disk = Config.Metrics.Disk ? ReadMetric("disk") : float.NaN;
        float gpu = Config.Metrics.Gpu ? ReadMetric("gpu") : float.NaN;
        float cpuTemperature = Config.Metrics.Temperature ? ReadMetric("temperature") : float.NaN;
        float gpuTemperature = Config.Metrics.Temperature ? ReadMetric("gpuTemperature") : float.NaN;
        NetworkRateText = NetworkRateTextFormatter.FormatPair(netUp, netDown);
        GridPerformanceText = GridPerformanceTextFormatter.Format(
            Config.Metrics.Gpu, gpu,
            Config.Metrics.Temperature, gpuTemperature);
        OnPropertyChanged(nameof(GridPerformanceToolTip));

        foreach (var pod in metricPods)
        {
            float value;
            string text;
            switch (pod.Key)
            {
                case "cpu":
                    value = cpu;
                    text = float.IsFinite(value) && cpuTemperature > 0
                        ? $"CPU {value:F0}%  -  {cpuTemperature:F0} C"
                        : float.IsFinite(value) ? $"CPU {value:F0}%  -  -- C" : "CPU --  -  -- C";
                    pod.ToolTipText = Config.Metrics.Temperature
                        ? _temperature?.SourceDescription ?? "Windows thermal zone unavailable"
                        : "CPU temperature disabled";
                    break;
                case "ram":
                    value = ram;
                    text = Config.Metrics.Temperature && Config.Metrics.RamTemperature
                        ? float.IsFinite(value) ? $"RAM {value:F0}%  -  {(float.IsFinite(ramTemperature) ? $"{ramTemperature:F0} C" : "-- C")}" : "RAM --  -  -- C"
                        : float.IsFinite(value) ? $"RAM {value:F0}%" : "RAM --";
                    pod.ToolTipText = Config.Metrics.RamTemperature ? _ramTemperatureReason : "RAM utilization";
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
                        ? $"GPU {gpu:F0}%  -  {gpuTemperature:F0} C"
                        : float.IsFinite(gpu) ? $"GPU {gpu:F0}%  -  -- C" : "GPU --  -  -- C";
                    pod.ToolTipText = Config.Metrics.Temperature ? _gpuTemperatureSource : "GPU temperature disabled";
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
        var agentPods = Pods.OfType<AgentPodViewModel>();
        if (!agentPods.Any()) return;

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
                    // SQLite agents: hiá»ƒn thá»‹ cost + tokens thay vÃ¬ % quota
                    double cost = last5h.Cost ?? 0;
                    double tok = (last5h.TokensTotal ?? 0) / 1e6;
                    valueText = $"${cost:F2}  -  {tok:F1}M";
                }
                else
                {
                    valueText = UsageTextFormatter.FormatBestQuota(pct5h, reset5h, pct7d, reset7d, showReset);
                }
            }

            if (data?.Error is not null)
                valueText = valueText == "--" ? "ERR" : $"{valueText}  -  ERR";
            else if (data is null && pod.Key == "Codex" && _usageInitializationError is not null)
                valueText = "ERR";
            valueText = UsageTextFormatter.FormatCompactAgentDisplay(data,
                diagnosticFailed: pod.Key == "Codex" && _usageInitializationError is not null);
            pod.ValueText = valueText;
            pod.FiveHourPercentage = pct5h ?? double.NaN;
            pod.SevenDayPercentage = pct7d ?? double.NaN;
            pod.FiveHourResetText = UsageTextFormatter.FormatResetTime(reset5h);
            pod.SevenDayResetText = UsageTextFormatter.FormatResetTime(reset7d);
            string fullValue = UsageTextFormatter.FormatAgentDisplay(data, showReset,
                diagnosticFailed: pod.Key == "Codex" && _usageInitializationError is not null);
            pod.ToolTipText = data?.Error is { Length: > 0 } error
                ? $"{fullValue}\nLast update failed: {error}"
                : data is null && pod.Key == "Codex" && _usageInitializationError is not null
                    ? _usageInitializationError
                : data?.LastUpdated is { } updated
                    ? $"{fullValue}\nUpdated {updated.LocalDateTime:g}"
                    : "Waiting for the first usage update";


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
                double latest = _sampler.GetLatest(key);
                if (double.IsFinite(latest)) return (float)latest;
            }
            catch (Exception ex)
            {
                Diagnostics.ReportReaderFailure($"sampler.{key}", ex);
            }
        }
        return float.NaN;
    }



    private static string FormatNetworkText(float upKbps, float downKbps)
    {
        if (!float.IsFinite(upKbps) || !float.IsFinite(downKbps)) return "UP --  DOWN --";
        string upText = upKbps > 1024 ? $"{upKbps / 1024f:F1} MB/s" : $"{upKbps:F0} KB/s";
        string downText = downKbps > 1024 ? $"{downKbps / 1024f:F1} MB/s" : $"{downKbps:F0} KB/s";
        return $"UP {upText} DOWN {downText}";
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
        _poller?.Dispose();
        foreach (var d in _metricDisposables)
        {
            try { d.Dispose(); }
            catch (Exception ex) { Diagnostics.ReportReaderFailure("disposable", ex); }
        }
    }
}
