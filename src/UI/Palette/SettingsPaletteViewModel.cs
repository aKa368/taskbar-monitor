using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using TaskbarMonitor.Config;
using TaskbarMonitor.UI.Layout;

namespace TaskbarMonitor.UI.Palette;

/// <summary>
/// ViewModel cho settings palette (kiểu command palette).
/// Build danh sách command từ ConfigData; search filter theo Label+Description;
/// Execute → gọi callback save (ConfigManager.Save) → hot-reload tự rebuild UI.
/// </summary>
public sealed class SettingsPaletteViewModel : INotifyPropertyChanged
{
    private readonly ConfigData _config;
    private readonly Action<ConfigData> _save;
    private string _searchText = "";

    public SettingsPaletteViewModel(ConfigData config, Action<ConfigData>? save = null)
    {
        _config = config;
        _save = save ?? (_ => { });
        Commands = BuildCommands();
    }

    public IReadOnlyList<SettingsCommand> Commands { get; }

    /// <summary>Refresh IsActiveValue/ValueText sau khi config đổi (hot-reload).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(FilteredCommands));
        foreach (var cmd in Commands)
        {
            cmd.NotifyStateChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                OnPropertyChanged(nameof(FilteredCommands));
            }
        }
    }

    /// <summary>Commands lọc theo SearchText (case-insensitive contains trên Label+Description).</summary>
    public IReadOnlyList<SettingsCommand> FilteredCommands
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                return Commands;
            }
            var q = _searchText.Trim();
            return Commands.Where(c =>
                c.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private List<SettingsCommand> BuildCommands()
    {
        var list = new List<SettingsCommand>();

        // Layout
        foreach (var kind in Enum.GetValues<WidgetLayoutKind>())
        {
            var info = LayoutDefinition.Get(kind);
            string name = kind.ToString();
            list.Add(new SettingsCommand
            {
                Id = $"choose-layout-{name}",
                Label = $"Layout: {info.Name}",
                Description = info.Description,
                Kind = SettingsCommandKind.ChooseLayout,
                IsActive = () => string.Equals(_config.Layout, name, StringComparison.OrdinalIgnoreCase),
                Execute = () =>
                {
                    _config.Layout = name;
                    _save(_config);
                }
            });
        }

        // Density
        foreach (var density in new[] { "Compact", "Comfortable" })
        {
            list.Add(new SettingsCommand
            {
                Id = $"set-density-{density}",
                Label = $"Density: {density}",
                Description = density == "Compact" ? "Padding & font nhỏ gọn" : "Padding & font thoải mái hơn",
                Kind = SettingsCommandKind.ChooseDensity,
                IsActive = () => string.Equals(_config.Density, density, StringComparison.OrdinalIgnoreCase),
                Execute = () =>
                {
                    _config.Density = density;
                    _save(_config);
                }
            });
        }

        // Metrics
        AddToggle(list, "toggle-metric-cpu", "Metric: CPU", "Bật/tắt pod CPU", SettingsCommandKind.ToggleMetric,
            () => _config.Metrics.Cpu, v => _config.Metrics.Cpu = v);
        AddToggle(list, "toggle-metric-ram", "Metric: RAM", "Bật/tắt pod RAM", SettingsCommandKind.ToggleMetric,
            () => _config.Metrics.Ram, v => _config.Metrics.Ram = v);
        AddToggle(list, "toggle-metric-network", "Metric: Network", "Bật/tắt pod Network", SettingsCommandKind.ToggleMetric,
            () => _config.Metrics.Network, v => _config.Metrics.Network = v);
        AddToggle(list, "toggle-metric-disk", "Metric: Disk", "Bật/tắt pod Disk", SettingsCommandKind.ToggleMetric,
            () => _config.Metrics.Disk, v => _config.Metrics.Disk = v);
        AddToggle(list, "toggle-metric-gpu", "Metric: GPU", "Bật/tắt pod GPU", SettingsCommandKind.ToggleMetric,
            () => _config.Metrics.Gpu, v => _config.Metrics.Gpu = v);
        AddToggle(list, "toggle-metric-temperature", "Metric: Temperature", "Bật/tắt pod nhiệt độ", SettingsCommandKind.ToggleMetric,
            () => _config.Metrics.Temperature, v => _config.Metrics.Temperature = v);

        // Agents
        AddToggle(list, "toggle-agent-commandcode", "Agent: CommandCode", "Bật/tắt pod CommandCode", SettingsCommandKind.ToggleAgent,
            () => _config.Agents.CommandCode, v => _config.Agents.CommandCode = v);
        AddToggle(list, "toggle-agent-opencode", "Agent: OpenCode", "Bật/tắt pod OpenCode", SettingsCommandKind.ToggleAgent,
            () => _config.Agents.OpenCode, v => _config.Agents.OpenCode = v);
        // The legacy config key is kept for backward compatibility; displayed quota is ChatGPT's.
        AddToggle(list, "toggle-agent-codex", "Agent: ChatGPT Usage", "Bật/tắt quota ChatGPT (5 giờ / 7 ngày)", SettingsCommandKind.ToggleAgent,
            () => _config.Agents.Codex, v => _config.Agents.Codex = v);
        AddToggle(list, "toggle-agent-antigravity", "Agent: Antigravity", "Bật/tắt pod Antigravity", SettingsCommandKind.ToggleAgent,
            () => _config.Agents.Antigravity, v => _config.Agents.Antigravity = v);
        AddToggle(list, "toggle-agent-claude", "Agent: Claude", "Bật/tắt pod Claude", SettingsCommandKind.ToggleAgent,
            () => _config.Agents.Claude, v => _config.Agents.Claude = v);

        // Interval
        foreach (var interval in new[] { 1, 2, 5 })
        {
            list.Add(new SettingsCommand
            {
                Id = $"set-interval-{interval}",
                Label = $"Interval: {interval}s",
                Description = $"Cập nhật mỗi {interval} giây",
                Kind = SettingsCommandKind.SetInterval,
                IsActive = () => _config.UpdateIntervalSeconds == interval,
                Execute = () =>
                {
                    _config.UpdateIntervalSeconds = interval;
                    _save(_config);
                }
            });
        }

        // Position
        foreach (var position in new[] { "Left", "Center", "Right" })
        {
            list.Add(new SettingsCommand
            {
                Id = $"set-position-{position}",
                Label = $"Position: {position}",
                Description = "Vị trí widget trong taskbar",
                Kind = SettingsCommandKind.SetPlacement,
                IsActive = () => string.Equals(_config.Position, position, StringComparison.OrdinalIgnoreCase),
                Execute = () =>
                {
                    _config.Position = position;
                    _save(_config);
                }
            });
        }

        // Toggles chung
        AddToggle(list, "set-autostart", "Autostart", "Tự động chạy khi đăng nhập Windows", SettingsCommandKind.ToggleAutostart,
            () => _config.Autostart, v => _config.Autostart = v);
        AddToggle(list, "set-showlabels", "Show labels", "Hiện label provider (CC/OC/...)", SettingsCommandKind.ToggleShowLabels,
            () => _config.ShowLabels, v => _config.ShowLabels = v);
        AddToggle(list, "set-showresetcountdown", "Show reset countdown", "Hiện 'reset in HH:MM'", SettingsCommandKind.ToggleShowResetCountdown,
            () => _config.ShowResetCountdown, v => _config.ShowResetCountdown = v);

        // Open config file
        list.Add(new SettingsCommand
        {
            Id = "open-config",
            Label = "Open config.json",
            Description = "Mở file config bằng editor mặc định",
            Kind = SettingsCommandKind.OpenConfigFile,
            Execute = () => OpenConfigFile()
        });

        // Quit
        list.Add(new SettingsCommand
        {
            Id = "quit",
            Label = "Quit TaskbarMonitor",
            Description = "Thoát ứng dụng",
            Kind = SettingsCommandKind.Quit,
            Execute = () => System.Windows.Application.Current?.Shutdown()
        });

        return list;
    }

    private void AddToggle(List<SettingsCommand> list, string id, string label, string desc,
        SettingsCommandKind kind, Func<bool> get, Action<bool> set)
    {
        list.Add(new SettingsCommand
        {
            Id = id,
            Label = label,
            Description = desc,
            Kind = kind,
            IsActive = get,
            Execute = () =>
            {
                set(!get());
                _save(_config);
            }
        });
    }

    private static void OpenConfigFile()
    {
        try
        {
            string path = ConfigManager.Instance.ConfigPath;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
