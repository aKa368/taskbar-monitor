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
    private string _fontFamily = TaskbarFontResolver.SafeFallback;
    private string _networkRateText = "UP -- DOWN --";
    private string _gridPerformanceText = string.Empty;
    private bool _isVertical;
    private bool _disposed;
    private bool _isMiniGameHud;
    private string _miniGameFpsText = "-- FPS";
    private string _miniGamePingText = "-- ms";
    private string _miniGameTempText = "--°C";
    private Brush _miniGameTempBrush = PodViewModel.NormalForegroundBrush;

    public bool IsMiniGameHud
    {
        get => _isMiniGameHud;
        set
        {
            if (SetField(ref _isMiniGameHud, value))
            {
                OnPropertyChanged(nameof(ShowDivider));
            }
        }
    }

    public string MiniGameFpsText
    {
        get => _miniGameFpsText;
        set => SetField(ref _miniGameFpsText, value);
    }

    public string MiniGamePingText
    {
        get => _miniGamePingText;
        set => SetField(ref _miniGamePingText, value);
    }

    public string MiniGameTempText
    {
        get => _miniGameTempText;
        set => SetField(ref _miniGameTempText, value);
    }

    public Brush MiniGameTempBrush
    {
        get => _miniGameTempBrush;
        set => SetField(ref _miniGameTempBrush, value);
    }

    public ConfigData Config => ConfigManager.Instance.Config;

    public bool IsVertical
    {
        get => _isVertical;
        set => SetField(ref _isVertical, value);
    }

    public bool IsFloatingVertical
    {
        get => Config.FloatingVertical;
        set
        {
            if (Config.FloatingVertical != value)
            {
                Config.FloatingVertical = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FloatingRotationAngle));
                OnPropertyChanged(nameof(ShowDivider));
                Tick();
            }
        }
    }

    public double FloatingRotationAngle => Config.FloatingVertical ? 90.0 : 0.0;

    public bool ShowDivider => AccountPods.Any(p => p.IsVisible) && SystemPods.Any(p => p.IsVisible);

    public System.Windows.HorizontalAlignment AccountHorizontalAlignment =>
        string.Equals(Config.Position, "Side", StringComparison.OrdinalIgnoreCase)
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

    public System.Windows.HorizontalAlignment SystemHorizontalAlignment =>
        string.Equals(Config.Position, "Side", StringComparison.OrdinalIgnoreCase)
            ? System.Windows.HorizontalAlignment.Right
            : System.Windows.HorizontalAlignment.Left;

    public ObservableCollection<PodViewModel> Pods => _layout.Pods;
    public IReadOnlyList<AgentPodViewModel> AccountPods => Rows.Count > 1 ? Rows[1].OfType<AgentPodViewModel>().ToList() : Pods.OfType<AgentPodViewModel>().ToList();
    public IReadOnlyList<MetricPodViewModel> SystemPods => Rows.Count > 0 ? Rows[0].OfType<MetricPodViewModel>().ToList() : Pods.OfType<MetricPodViewModel>().ToList();
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

    public double FontSize
    {
        get => Config.FontSize > 0 ? Config.FontSize : 11.5;
        set
        {
            if (Math.Abs(Config.FontSize - value) > 0.1)
            {
                Config.FontSize = value;
                ConfigManager.Instance.Save();
                OnPropertyChanged(nameof(FontSize));
                OnPropertyChanged(nameof(VerticalMetricFontSize));
                OnPropertyChanged(nameof(VerticalAgentTitleFontSize));
                OnPropertyChanged(nameof(VerticalSubFontSize));
                OnPropertyChanged(nameof(VerticalPrimaryFontSize));
                OnPropertyChanged(nameof(VerticalSecondaryFontSize));
            }
        }
    }

    public double VerticalMetricFontSize => Math.Max(12.0, Math.Round(FontSize * 0.8, 1));
    public double VerticalAgentTitleFontSize => Math.Max(11.5, Math.Round(FontSize * 0.75, 1));
    public double VerticalSubFontSize => Math.Max(10.5, Math.Round(FontSize * 0.7, 1));

    public double VerticalPrimaryFontSize => VerticalMetricFontSize;
    public double VerticalSecondaryFontSize => VerticalMetricFontSize;

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
            return $"GPU utilization - busiest physical engine - Temperature: {_gpuTemperatureSource}";
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
            OnPropertyChanged(nameof(GridAgents));
            OnPropertyChanged(nameof(GridPrimaryAgent));
            OnPropertyChanged(nameof(GridSecondaryAgents));
            OnPropertyChanged(nameof(FontSize));
            OnPropertyChanged(nameof(VerticalMetricFontSize));
            OnPropertyChanged(nameof(VerticalAgentTitleFontSize));
            OnPropertyChanged(nameof(VerticalSubFontSize));
            OnPropertyChanged(nameof(VerticalPrimaryFontSize));
            OnPropertyChanged(nameof(VerticalSecondaryFontSize));
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };

        _timer = SharedTaskbarTickSource.Subscribe(
            Tick,
            TimeSpan.FromSeconds(Math.Max(1, Config.UpdateIntervalSeconds)));

        ApplyConfigSettings(); // cần _timer đã tôn tại (đặt interval theo layout)

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
            _sampler = null; // fallback random nếu không khởi tạo được
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _usageInitializationError = $"ChatGPT usage refresh did not complete ({ex.GetType().Name}).";
        }

        App.Current?.Dispatcher.BeginInvoke(() => Tick());
    }

    private void OnConfigReloaded(object? sender, ConfigData newConfig)
    {
        App.Current?.Dispatcher.InvokeAsync(() =>
        {
            InitMetricsSampler();
            _poller?.Reconfigure(CreatePollerOptions(newConfig));
            ApplyConfigSettings(forceRebuild: true); // config reload → ép rebuild pods
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(1, newConfig.UpdateIntervalSeconds));
            OnPropertyChanged(string.Empty);
            Tick();
        });
    }

    private static UsagePollerOptions CreatePollerOptions(ConfigData config) => new()
    {
        CommandCodeEnabled = config.Agents.CommandCode,
        OpenCodeEnabled = config.Agents.OpenCode,
        AntigravityEnabled = config.Agents.Antigravity,
        ClaudeEnabled = config.Agents.Claude
    };

    private void ApplyConfigSettings(bool forceRebuild = false)
    {
        FontFamily = TaskbarFontResolver.Resolve(Config.FontFamily);
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
        bool isApuFallback = false;
        if (!float.IsFinite(cpuTemperature) && float.IsFinite(gpuTemperature) && gpuTemperature > 0)
        {
            cpuTemperature = gpuTemperature;
            isApuFallback = true;
        }
        NetworkRateText = NetworkRateTextFormatter.FormatPair(netUp, netDown);
        GridPerformanceText = GridPerformanceTextFormatter.Format(
            Config.Metrics.Gpu, gpu,
            Config.Metrics.Temperature, gpuTemperature);
        OnPropertyChanged(nameof(GridPerformanceToolTip));

        bool isGaming = Config.Metrics.GameOverlay && GamePerformanceMonitor.Instance.IsGameRunning;
        int fps = GamePerformanceMonitor.Instance.CurrentFps;
        int ping = GamePerformanceMonitor.Instance.CurrentPingMs;
        string gameName = GamePerformanceMonitor.Instance.CurrentGameName;

        // Cập nhật MiniGameHud text & alert
        MiniGameFpsText = isGaming ? $"{fps} FPS" : "-- FPS";
        MiniGamePingText = isGaming && ping > 0 ? $"{ping} ms" : "-- ms";
        float gameTemp = float.IsFinite(cpuTemperature) && cpuTemperature > 0
            ? cpuTemperature
            : (float.IsFinite(gpuTemperature) && gpuTemperature > 0 ? gpuTemperature : 0);
        MiniGameTempText = gameTemp > 0 ? $"{gameTemp:F0}°C" : "--°C";
        bool isGameTempDanger = gameTemp >= 85;
        bool isGameTempWarning = gameTemp >= 80;
        MiniGameTempBrush = isGameTempDanger ? PodViewModel.DangerForegroundBrush
            : (isGameTempWarning ? PodViewModel.WarningForegroundBrush : PodViewModel.NormalForegroundBrush);

        foreach (var pod in metricPods)
        {
            float value;
            string text;
            switch (pod.Key)
            {
                case "fps":
                    pod.IsVisible = isGaming;
                    text = isGaming ? $"{fps} FPS" : "-- FPS";
                    pod.Line1 = $"{fps} FPS";
                    pod.ToolTipText = isGaming ? $"Game: {gameName}\nFramerate: {fps} FPS" : "No game running";
                    pod.SetAlertState(isGaming && fps > 0 && fps < 30, isGaming && fps > 0 && fps < 55);
                    break;
                case "ping":
                    pod.IsVisible = isGaming;
                    text = isGaming ? (ping > 0 ? $"{ping} ms" : "-- ms") : "-- ms";
                    pod.Line1 = isGaming && ping > 0 ? $"{ping} ms" : "-- ms";
                    pod.ToolTipText = isGaming
                        ? $"Game: {gameName}\nServer: {GamePerformanceMonitor.Instance.CurrentServerEndpoint}\nLatency: {(ping > 0 ? $"{ping}ms" : "measuring...")}"
                        : "No active game connection";
                    pod.SetAlertState(isGaming && ping >= 150, isGaming && ping >= 80);
                    break;
                case "cpu_gpu_ram":
                    value = cpu;
                    float sysTemp = float.IsFinite(cpuTemperature) && cpuTemperature > 0
                        ? cpuTemperature
                        : (float.IsFinite(gpuTemperature) && gpuTemperature > 0 ? gpuTemperature : 0);
                    string cVal = float.IsFinite(cpu) ? $"{cpu:F0}%" : "--";
                    string gVal = float.IsFinite(gpu) ? $"{gpu:F0}%" : "--";
                    string rVal = float.IsFinite(ram) ? $"{ram:F0}%" : "--";
                    string sTemp = sysTemp > 0 ? $" · {sysTemp:F0}°C" : "";

                    text = $"C {cVal}  G {gVal}  R {rVal}{sTemp}";
                    pod.Line1 = IsFloatingVertical ? $"C {cVal}" : $"C {cVal}  G {gVal}  R {rVal}";
                    pod.Line2 = IsFloatingVertical ? $"G {gVal} R {rVal}" : (sysTemp > 0 ? $"{sysTemp:F0}°C" : "--°C");
                    pod.HasLine2 = true;
                    pod.ToolTipText = $"System Hardware\nCPU: {cVal} | GPU: {gVal} | RAM: {rVal}\nPackage Temperature: {(sysTemp > 0 ? $"{sysTemp:F0}°C" : "unavailable")}";
                    bool isCgrDanger = (float.IsFinite(cpu) && cpu >= 95) || (float.IsFinite(gpu) && gpu >= 95) || (float.IsFinite(ram) && ram >= 95) || sysTemp >= 85;
                    bool isCgrWarning = (float.IsFinite(cpu) && cpu >= 85) || (float.IsFinite(gpu) && gpu >= 85) || (float.IsFinite(ram) && ram >= 90) || sysTemp >= 80;
                    pod.SetAlertState(isCgrDanger, isCgrWarning);
                    break;
                case "cpu_gpu":
                    value = cpu;
                    float apuTemp = float.IsFinite(cpuTemperature) && cpuTemperature > 0
                        ? cpuTemperature
                        : (float.IsFinite(gpuTemperature) && gpuTemperature > 0 ? gpuTemperature : 0);
                    string cpuStr = float.IsFinite(cpu) ? $"{cpu:F0}%" : "--";
                    string gpuStr = float.IsFinite(gpu) ? $"{gpu:F0}%" : "--";
                    string tempStr = apuTemp > 0 ? $" · {apuTemp:F0}°C" : "";

                    text = $"C {cpuStr}  G {gpuStr}{tempStr}";
                    pod.Line1 = $"C {cpuStr}";
                    pod.Line2 = IsFloatingVertical ? $"G {gpuStr}" : $"G {gpuStr}{tempStr}";
                    pod.HasLine2 = true;
                    pod.ToolTipText = $"APU Package (AMD Ryzen + Radeon iGPU)\nCPU: {cpuStr} | GPU: {gpuStr}\nTemperature: {(apuTemp > 0 ? $"{apuTemp:F0}°C" : "unavailable")}";
                    bool isApuDanger = (float.IsFinite(cpu) && cpu >= 95) || (float.IsFinite(gpu) && gpu >= 95) || apuTemp >= 85;
                    bool isApuWarning = (float.IsFinite(cpu) && cpu >= 85) || (float.IsFinite(gpu) && gpu >= 85) || apuTemp >= 80;
                    pod.SetAlertState(isApuDanger, isApuWarning);
                    break;
                case "cpu":
                    value = cpu;
                    text = float.IsFinite(value) && cpuTemperature > 0
                        ? $"C {value:F0}% · {cpuTemperature:F0}°C"
                        : float.IsFinite(value) ? $"C {value:F0}%" : "C --";
                    pod.ToolTipText = Config.Metrics.Temperature
                        ? isApuFallback
                            ? $"CPU Package: {cpuTemperature:F0}°C (APU shared die telemetry)"
                            : _temperature?.SourceDescription ?? "Windows thermal zone unavailable"
                        : "CPU temperature disabled";
                    pod.Line1 = float.IsFinite(value) ? $"C {value:F0}%" : "C --";
                    pod.Line2 = cpuTemperature > 0 ? $"{cpuTemperature:F0}°C" : "--°C";
                    pod.HasLine2 = true;
                    bool isCpuDanger = (float.IsFinite(value) && value >= 95) || cpuTemperature >= 85;
                    bool isCpuWarning = (float.IsFinite(value) && value >= 85) || cpuTemperature >= 80;
                    pod.SetAlertState(isCpuDanger, isCpuWarning);
                    break;
                case "gpu":
                    value = gpu;
                    text = float.IsFinite(value) && gpuTemperature > 0
                        ? $"G {value:F0}% · {gpuTemperature:F0}°C"
                        : float.IsFinite(value) ? $"G {value:F0}%" : "G --";
                    pod.ToolTipText = Config.Metrics.Temperature
                        ? $"GPU: {gpuTemperature:F0}°C ({_gpuTemperatureSource})"
                        : "GPU temperature disabled";
                    pod.Line1 = float.IsFinite(value) ? $"G {value:F0}%" : "G --";
                    pod.Line2 = gpuTemperature > 0 ? $"{gpuTemperature:F0}°C" : "--°C";
                    pod.HasLine2 = true;
                    bool isGpuDanger = (float.IsFinite(value) && value >= 95) || gpuTemperature >= 85;
                    bool isGpuWarning = (float.IsFinite(value) && value >= 85) || gpuTemperature >= 80;
                    pod.SetAlertState(isGpuDanger, isGpuWarning);
                    break;
                case "ram":
                    value = ram;
                    text = Config.Metrics.Temperature && Config.Metrics.RamTemperature
                        ? float.IsFinite(value) ? $"R {value:F0}% · {(float.IsFinite(ramTemperature) ? $"{ramTemperature:F0}°C" : "--°C")}" : "R --"
                        : float.IsFinite(value) ? $"R {value:F0}%" : "R --";
                    pod.ToolTipText = Config.Metrics.RamTemperature ? _ramTemperatureReason : "RAM utilization";
                    pod.Line1 = float.IsFinite(value) ? $"R {value:F0}%" : "R --";
                    bool hasRamTemp = Config.Metrics.Temperature && Config.Metrics.RamTemperature && float.IsFinite(ramTemperature);
                    pod.Line2 = hasRamTemp ? $"{ramTemperature:F0}°C" : "";
                    pod.HasLine2 = hasRamTemp;
                    bool isRamDanger = float.IsFinite(value) && value >= 95;
                    bool isRamWarning = float.IsFinite(value) && value >= 90;
                    pod.SetAlertState(isRamDanger, isRamWarning);
                    break;
                case "network":
                    value = float.IsFinite(netUp) && float.IsFinite(netDown)
                        ? Math.Min(100f, (netUp + netDown) / 10f) : 0;
                    text = FormatNetworkText(netUp, netDown);
                    pod.Line1 = float.IsFinite(netUp) ? $"↑ {FormatNetworkRate(netUp)}" : "↑ --";
                    pod.Line2 = float.IsFinite(netDown) ? $"↓ {FormatNetworkRate(netDown)}" : "↓ --";
                    pod.HasLine2 = true;
                    pod.SetAlertState(false, false);
                    break;
                case "disk":
                    value = disk;
                    text = float.IsFinite(disk) ? $"DISK {disk:F0}%" : "DISK --";
                    pod.Line1 = "DISK";
                    pod.Line2 = float.IsFinite(disk) ? $"{disk:F0}%" : "--";
                    pod.HasLine2 = true;
                    bool isDiskDanger = float.IsFinite(disk) && disk >= 95;
                    bool isDiskWarning = float.IsFinite(disk) && disk >= 90;
                    pod.SetAlertState(isDiskDanger, isDiskWarning);
                    break;
                default:
                    value = 0;
                    text = pod.Label;
                    pod.Line1 = pod.Label;
                    pod.Line2 = string.Empty;
                    pod.HasLine2 = false;
                    pod.SetAlertState(false, false);
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
        foreach (var pod in agentPods)
        {
            var data = _poller?.Get(ToAgentId(pod.Key));

            double? pct5h = data?.UsedPercent5h;
            double? pct7d = data?.UsedPercent7d;
            DateTime? reset5h = data?.ResetsAt5h?.LocalDateTime;
            DateTime? reset7d = data?.ResetsAt7d?.LocalDateTime;

            string fullValue = UsageTextFormatter.FormatAgentDisplay(data, showReset);
            pod.ValueText = UsageTextFormatter.FormatCompactAgentDisplay(data);
            pod.Line1 = pod.Label;
            pod.Line2 = pct5h.HasValue ? $"{pct5h.Value:F0}%" : pod.ValueText;
            pod.HasLine2 = true;
            pod.ToolTipText = data?.Error is { Length: > 0 } error
                ? $"{fullValue}\nLast update failed: {error}"
                : data?.LastUpdated is { } updated
                    ? $"{fullValue}\nUpdated {updated.LocalDateTime:g}"
                    : "Waiting for the first usage update";

            if (data?.Source != "api")
            {
                pod.FiveHourPercentage = pct5h ?? double.NaN;
                pod.SevenDayPercentage = pct7d ?? double.NaN;
                pod.FiveHourResetText = UsageTextFormatter.FormatResetTime(reset5h);
                pod.SevenDayResetText = UsageTextFormatter.FormatResetTime(reset7d);
                pod.Line3 = pod.FiveHourResetText;
            }
            else
            {
                pod.FiveHourPercentage = double.NaN;
                pod.SevenDayPercentage = double.NaN;
                pod.Line3 = string.Empty;
            }
        }
    }


    private static string ToAgentId(string key) => key switch
    {
        "CommandCode" => AgentIds.CommandCode,
        "OpenCode" => AgentIds.OpenCode,
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
        if (!float.IsFinite(upKbps) || !float.IsFinite(downKbps)) return "↑ --  ↓ --";
        return $"↑ {FormatNetworkRate(upKbps)}  ↓ {FormatNetworkRate(downKbps)}";
    }

    private static string FormatNetworkRate(float kbps) => kbps >= 1024 ? $"{kbps / 1024f:F1}M" : $"{kbps:F0}K";


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