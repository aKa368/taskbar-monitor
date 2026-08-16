using System.Windows;
using System.Windows.Controls;
using TaskbarMonitor.Config;
using TaskbarMonitor.UI.Layout;
using TaskbarMonitor.UI.Palette;

namespace TaskbarMonitor.UI;

/// <summary>
/// Context menu chuá»™t pháº£i cho widget â€” settings trá»±c quan toÃ n bá»™.
/// Submenu cÃ³ checkmark theo tráº¡ng thÃ¡i config hiá»‡n táº¡i.
/// Má»i thay Ä‘á»•i â†’ ConfigManager.Save() â†’ hot-reload tá»± rebuild UI.
/// </summary>
public static class TaskbarContextMenu
{
    public static ContextMenu Build()
    {
        var menu = new ContextMenu();

        // Header: Settings Palette (command palette)
        var paletteItem = new MenuItem { Header = "âš™ Settings Paletteâ€¦" };
        paletteItem.Click += (_, _) => ShowSettingsPalette();
        menu.Items.Add(paletteItem);
        menu.Items.Add(new Separator());

        // Layout submenu
        menu.Items.Add(BuildLayoutMenu());
        menu.Items.Add(BuildDensityMenu());
        menu.Items.Add(BuildMetricsMenu());
        menu.Items.Add(BuildAgentsMenu());
        menu.Items.Add(BuildIntervalMenu());
        menu.Items.Add(BuildPositionMenu());
        menu.Items.Add(new Separator());

        // Checkbox toggles
        menu.Items.Add(BuildCheckItem("Autostart", () => ConfigManager.Instance.Config.Autostart, v =>
        {
            ConfigManager.Instance.Config.Autostart = v;
            ConfigManager.Instance.Save();
        }));
        menu.Items.Add(BuildCheckItem("Show labels", () => ConfigManager.Instance.Config.ShowLabels, v =>
        {
            ConfigManager.Instance.Config.ShowLabels = v;
            ConfigManager.Instance.Save();
        }));
        menu.Items.Add(BuildCheckItem("Show reset countdown", () => ConfigManager.Instance.Config.ShowResetCountdown, v =>
        {
            ConfigManager.Instance.Config.ShowResetCountdown = v;
            ConfigManager.Instance.Save();
        }));
        menu.Items.Add(new Separator());

        // Open config + Quit
        var openConfig = new MenuItem { Header = "Open config.json" };
        openConfig.Click += (_, _) => OpenConfigFile();
        menu.Items.Add(openConfig);

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Application.Current?.Shutdown();
        menu.Items.Add(quit);

        return menu;
    }

    private static MenuItem BuildLayoutMenu()
    {
        var item = new MenuItem { Header = "Layout" };
        foreach (var kind in new[] { WidgetLayoutKind.Grid })
        {
            var info = LayoutDefinition.Get(kind);
            var sub = new MenuItem
            {
                Header = $"{info.Name}  â€”  {info.Description}",
                IsCheckable = true,
                IsChecked = IsCurrent(kind)
            };
            sub.Click += (_, _) => SetLayout(kind);
            item.Items.Add(sub);
        }
        return item;
    }

    private static MenuItem BuildDensityMenu()
    {
        var item = new MenuItem { Header = "Density" };
        foreach (var density in new[] { "Compact", "Comfortable" })
        {
            var sub = new MenuItem
            {
                Header = density,
                IsCheckable = true,
                IsChecked = string.Equals(ConfigManager.Instance.Config.Density, density, StringComparison.OrdinalIgnoreCase)
            };
            sub.Click += (_, _) => SetDensity(density);
            item.Items.Add(sub);
        }
        return item;
    }

    private static MenuItem BuildMetricsMenu()
    {
        var item = new MenuItem { Header = "Metrics" };
        AddMetricToggle(item, "CPU", c => c.Metrics.Cpu);
        AddMetricToggle(item, "RAM", c => c.Metrics.Ram);
        AddMetricToggle(item, "Network", c => c.Metrics.Network);
        AddMetricToggle(item, "Disk", c => c.Metrics.Disk);
        AddMetricToggle(item, "GPU", c => c.Metrics.Gpu);
        AddMetricToggle(item, "Temperature", c => c.Metrics.Temperature);
        return item;
    }

    private static MenuItem BuildAgentsMenu()
    {
        var item = new MenuItem { Header = "Agents" };
        AddAgentToggle(item, "CommandCode", c => c.Agents.CommandCode);
        AddAgentToggle(item, "OpenCode", c => c.Agents.OpenCode);
        AddAgentToggle(item, "Codex", c => c.Agents.Codex);
        AddAgentToggle(item, "Antigravity", c => c.Agents.Antigravity);
        AddAgentToggle(item, "Claude", c => c.Agents.Claude);
        return item;
    }

    private static MenuItem BuildIntervalMenu()
    {
        var item = new MenuItem { Header = "Interval" };
        foreach (var interval in new[] { 1, 2, 5 })
        {
            var sub = new MenuItem
            {
                Header = $"{interval}s",
                IsCheckable = true,
                IsChecked = ConfigManager.Instance.Config.UpdateIntervalSeconds == interval
            };
            sub.Click += (_, _) => SetInterval(interval);
            item.Items.Add(sub);
        }
        return item;
    }

    private static MenuItem BuildPositionMenu()
    {
        var item = new MenuItem { Header = "Position" };
        foreach (var position in new[] { "Left", "Center", "Right" })
        {
            var sub = new MenuItem
            {
                Header = position,
                IsCheckable = true,
                IsChecked = string.Equals(ConfigManager.Instance.Config.Position, position, StringComparison.OrdinalIgnoreCase)
            };
            sub.Click += (_, _) => SetPosition(position);
            item.Items.Add(sub);
        }
        return item;
    }

    private static void AddMetricToggle(MenuItem parent, string label, Func<ConfigData, bool> get)
    {
        var sub = new MenuItem
        {
            Header = label,
            IsCheckable = true,
            IsChecked = get(ConfigManager.Instance.Config)
        };
        sub.Click += (_, _) =>
        {
            var cfg = ConfigManager.Instance.Config;
            SetMetric(cfg, label, !get(cfg));
            ConfigManager.Instance.Save();
        };
        parent.Items.Add(sub);
    }

    private static void AddAgentToggle(MenuItem parent, string label, Func<ConfigData, bool> get)
    {
        var sub = new MenuItem
        {
            Header = label,
            IsCheckable = true,
            IsChecked = get(ConfigManager.Instance.Config)
        };
        sub.Click += (_, _) =>
        {
            var cfg = ConfigManager.Instance.Config;
            SetAgent(cfg, label, !get(cfg));
            ConfigManager.Instance.Save();
        };
        parent.Items.Add(sub);
    }

    private static MenuItem BuildCheckItem(string header, Func<bool> get, Action<bool> set)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = get()
        };
        item.Click += (_, _) => set(!get());
        return item;
    }

    // --- Helpers mutate ConfigData ---

    private static bool IsCurrent(WidgetLayoutKind kind)
        => string.Equals(ConfigManager.Instance.Config.Layout, kind.ToString(), StringComparison.OrdinalIgnoreCase);

    private static void SetLayout(WidgetLayoutKind kind)
    {
        ConfigManager.Instance.Config.Layout = kind.ToString();
        ConfigManager.Instance.Save();
    }

    private static void SetDensity(string density)
    {
        ConfigManager.Instance.Config.Density = density;
        ConfigManager.Instance.Save();
    }

    private static void SetInterval(int interval)
    {
        ConfigManager.Instance.Config.UpdateIntervalSeconds = interval;
        ConfigManager.Instance.Save();
    }

    private static void SetPosition(string position)
    {
        ConfigManager.Instance.Config.Position = position;
        ConfigManager.Instance.Save();
    }

    private static void SetMetric(ConfigData cfg, string label, bool value)
    {
        switch (label)
        {
            case "CPU": cfg.Metrics.Cpu = value; break;
            case "RAM": cfg.Metrics.Ram = value; break;
            case "Network": cfg.Metrics.Network = value; break;
            case "Disk": cfg.Metrics.Disk = value; break;
            case "GPU": cfg.Metrics.Gpu = value; break;
            case "Temperature": cfg.Metrics.Temperature = value; break;
        }
    }

    private static void SetAgent(ConfigData cfg, string label, bool value)
    {
        switch (label)
        {
            case "CommandCode": cfg.Agents.CommandCode = value; break;
            case "OpenCode": cfg.Agents.OpenCode = value; break;
            case "Codex": cfg.Agents.Codex = value; break;
            case "Antigravity": cfg.Agents.Antigravity = value; break;
            case "Claude": cfg.Agents.Claude = value; break;
        }
    }

    public static void ShowSettingsPalette()
    {
        var existing = Application.Current?.Windows.OfType<SettingsPaletteWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsPaletteWindow();
        window.Show();
        window.Activate();
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
}
