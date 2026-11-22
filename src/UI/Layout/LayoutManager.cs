using System.Collections.ObjectModel;
using TaskbarMonitor.Config;
using TaskbarMonitor.Metrics;

namespace TaskbarMonitor.UI.Layout;

/// <summary>
/// Engine build pod list theo layout preset. Cache layout đã build;
/// chỉ rebuild khi layout/density thay đổi (không rebuild mỗi tick).
/// TwoLine → Rows = 2 hàng (metrics trên, agents dưới).
/// </summary>
public sealed class LayoutManager
{
    /// <summary>Raise khi danh sách pod bị rebuild (layout/density/config đổi).</summary>
    public event EventHandler? LayoutChanged;

    private readonly ObservableCollection<PodViewModel> _pods = new();
    private readonly List<IReadOnlyList<PodViewModel>> _rows = new();
    private WidgetLayoutKind? _currentKind;
    private string? _currentDensity;
    private ConfigData? _currentConfig;

    /// <summary>Pods hiển thị (1 hàng — Compact/Minimal/Bars/Charts/AgentCentric). ObservableCollection để ItemsControl tự cập nhật khi rebuild.</summary>
    public ObservableCollection<PodViewModel> Pods => _pods;

    /// <summary>Rows (2 hàng — TwoLine).</summary>
    public IReadOnlyList<IReadOnlyList<PodViewModel>> Rows => _rows;

    public LayoutInfo CurrentLayout => _currentKind is { } k ? LayoutDefinition.Get(k) : LayoutDefinition.Get(WidgetLayoutKind.Compact);

    public bool IsTwoRow => _currentKind == WidgetLayoutKind.TwoLine;

    /// <summary>
    /// Build pod list theo layout. Nếu layout + density + config-agent giống hệt lần trước → cache hit (no-op).
    /// forceRebuild=true khi config reload (file thay đổi) — ép rebuild dù giá trị giống.
    /// </summary>
    public void Apply(WidgetLayoutKind kind, ConfigData config, bool forceRebuild = false)
    {
        // Cache: chỉ rebuild khi layout/density hoặc cấu hình agents đổi.
        bool agentsSame = _currentConfig != null
            && _currentConfig.Agents.CommandCode == config.Agents.CommandCode
            && _currentConfig.Agents.OpenCode == config.Agents.OpenCode
            && _currentConfig.Agents.Antigravity == config.Agents.Antigravity
            && _currentConfig.Agents.Claude == config.Agents.Claude;
        bool metricsSame = _currentConfig != null
            && _currentConfig.Metrics.Cpu == config.Metrics.Cpu
            && _currentConfig.Metrics.Ram == config.Metrics.Ram
            && _currentConfig.Metrics.Network == config.Metrics.Network
            && _currentConfig.Metrics.Disk == config.Metrics.Disk
            && _currentConfig.Metrics.Gpu == config.Metrics.Gpu
            && _currentConfig.Metrics.Temperature == config.Metrics.Temperature
            && _currentConfig.Metrics.RamTemperature == config.Metrics.RamTemperature
            && _currentConfig.Metrics.GameOverlay == config.Metrics.GameOverlay;

        if (!forceRebuild
            && _currentKind == kind
            && _currentDensity == config.Density
            && agentsSame
            && metricsSame)
        {
            return; // cache hit
        }

        var def = LayoutDefinition.Get(kind);
        _pods.Clear();
        _rows.Clear();

        var metricPods = new List<PodViewModel>();
        var agentPods = new List<PodViewModel>();

        if (def.ShowMetricPods)
        {
            bool mergeApu = HardwareTopology.IsSharedApu && config.Metrics.Cpu && config.Metrics.Gpu;
            if (mergeApu)
            {
                metricPods.Add(new MetricPodViewModel("cpu_gpu", "APU"));
                if (config.Metrics.Ram) metricPods.Add(new MetricPodViewModel("ram", "R"));
                if (config.Metrics.Network) metricPods.Add(new MetricPodViewModel("network", "NET"));
                if (config.Metrics.Disk) metricPods.Add(new MetricPodViewModel("disk", "DISK"));
            }
            else
            {
                // Grid is deliberately ordered by stable columns: CPU/RAM,
                // GPU/Disk, then Network last in its own outer column.
                var metricOrder = kind == WidgetLayoutKind.Grid
                    ? new[] { ("cpu", "C", config.Metrics.Cpu), ("ram", "R", config.Metrics.Ram),
                              ("gpu", "G", config.Metrics.Gpu), ("disk", "DISK", config.Metrics.Disk),
                              ("network", "NET", config.Metrics.Network) }
                    : new[] { ("cpu", "C", config.Metrics.Cpu), ("ram", "R", config.Metrics.Ram),
                              ("network", "NET", config.Metrics.Network), ("disk", "DISK", config.Metrics.Disk),
                              ("gpu", "G", config.Metrics.Gpu) };
                foreach (var (key, label, enabled) in metricOrder)
                    if (enabled) metricPods.Add(new MetricPodViewModel(key, label));
            }

            if (config.Metrics.GameOverlay && kind != WidgetLayoutKind.Grid)
            {
                metricPods.Add(new MetricPodViewModel("fps", "FPS") { IsVisible = false });
                metricPods.Add(new MetricPodViewModel("ping", "PING") { IsVisible = false });
            }
        }

        if (def.ShowAgentPods)
        {
            var agentOrder = kind == WidgetLayoutKind.Grid
                ? new[] { ("Claude", "CL", config.Agents.Claude),
                          ("OpenCode", "OC", config.Agents.OpenCode), ("CommandCode", "CC", config.Agents.CommandCode),
                          ("Antigravity", "AGY", config.Agents.Antigravity) }
                : new[] { ("CommandCode", "CC", config.Agents.CommandCode), ("OpenCode", "OC", config.Agents.OpenCode),
                          ("Antigravity", "AGY", config.Agents.Antigravity),
                          ("Claude", "CL", config.Agents.Claude) };
            foreach (var (key, label, enabled) in agentOrder)
                if (enabled) agentPods.Add(new AgentPodViewModel(key, label) { ColorHex = GetAgentColor(config, key) });
        }

        if (def.TwoRow)
        {
            _rows.Add(metricPods);
            _rows.Add(agentPods);
            foreach (var p in metricPods) _pods.Add(p);
            foreach (var p in agentPods) _pods.Add(p);
        }
        else
        {
            _rows.Add(metricPods);
            _rows.Add(agentPods);
            foreach (var p in metricPods) _pods.Add(p);
            foreach (var p in agentPods) _pods.Add(p);
        }

        _currentKind = kind;
        _currentDensity = config.Density;
        _currentConfig = config;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string GetAgentColor(ConfigData config, string key) => key switch
    {
        "CommandCode" => config.Colors.CommandCode,
        "OpenCode" => config.Colors.OpenCode,
        "Antigravity" => config.Colors.Antigravity,
        "Claude" => config.Colors.Claude,
        _ => "#6F42C1"
    };
}
