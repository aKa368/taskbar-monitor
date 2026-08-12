using WpfApplication = System.Windows.Application;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsMouseButtons = System.Windows.Forms.MouseButtons;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using FormsToolStripSeparator = System.Windows.Forms.ToolStripSeparator;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using TaskbarMonitor.Config;
using TaskbarMonitor.UI;

namespace TaskbarMonitor;

/// <summary>
/// Native notification-area controls for the taskbar-hosted app. The primary
/// settings surface is a lightweight WinForms menu owned by the tray icon, so
/// changing a setting does not allocate a WPF palette window or refresh timer.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly FormsNotifyIcon _icon;
    private bool _disposed;

    public TrayIconService()
    {
        _icon = new FormsNotifyIcon
        {
            Icon = DrawingSystemIcons.Application,
            Text = "TaskbarMonitor",
            ContextMenuStrip = BuildTrayMenu(),
            Visible = true
        };

        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == FormsMouseButtons.Left)
            {
                // Left click exposes the same native, tray-attached menu as
                // right click. Unlike SettingsPaletteWindow, this owns no WPF
                // window or DispatcherTimer while it is closed.
                ShowTrayMenu();
            }
        };
        // No DoubleClick handler: a double-click already raises MouseClick first,
        // and opening the same native menu twice would be redundant.
    }

    private void ShowTrayMenu()
    {
        // The notification area can invoke callbacks from Explorer. The menu is
        // WinForms-owned, while ConfigManager safely notifies WPF after Save.
        _icon.ContextMenuStrip?.Show(System.Windows.Forms.Control.MousePosition);
    }

    private static FormsContextMenuStrip BuildTrayMenu()
    {
        var menu = new FormsContextMenuStrip();
        menu.Items.Add(BuildChoiceMenu(
            "Layout",
            new[] { "Compact", "TwoLine", "Grid" },
            () => ConfigManager.Instance.Config.Layout,
            value => ConfigManager.Instance.Config.Layout = value));
        menu.Items.Add(BuildChoiceMenu(
            "Position",
            new[] { "Left", "Center", "Right" },
            () => ConfigManager.Instance.Config.Position,
            value => ConfigManager.Instance.Config.Position = value));
        menu.Items.Add(BuildChoiceMenu(
            "Refresh interval",
            new[] { "1s", "2s", "5s" },
            () => $"{ConfigManager.Instance.Config.UpdateIntervalSeconds}s",
            value => ConfigManager.Instance.Config.UpdateIntervalSeconds = int.Parse(value[..^1])));

        menu.Items.Add(BuildMetricsMenu());
        menu.Items.Add(BuildAgentsMenu());

        var display = new FormsToolStripMenuItem("Display");
        display.DropDownItems.Add(BuildToggle("Show labels", c => c.ShowLabels, (c, value) => c.ShowLabels = value));
        display.DropDownItems.Add(BuildToggle("Show reset countdown", c => c.ShowResetCountdown, (c, value) => c.ShowResetCountdown = value));
        display.DropDownItems.Add(BuildToggle("Autostart", c => c.Autostart, (c, value) => c.Autostart = value));
        menu.Items.Add(display);

        menu.Items.Add(new FormsToolStripSeparator());
        menu.Items.Add(CreateAction("Advanced settings palette…", () =>
            WpfApplication.Current?.Dispatcher.BeginInvoke(TaskbarContextMenu.ShowSettingsPalette)));
        menu.Items.Add(CreateAction("Open config.json", OpenConfig));
        menu.Items.Add(new FormsToolStripSeparator());
        menu.Items.Add(CreateAction("Exit", () => WpfApplication.Current?.Shutdown()));
        return menu;
    }

    private static FormsToolStripMenuItem BuildMetricsMenu()
    {
        var metrics = new FormsToolStripMenuItem("Metrics");
        metrics.DropDownItems.Add(BuildToggle("CPU", c => c.Metrics.Cpu, (c, value) => c.Metrics.Cpu = value));
        metrics.DropDownItems.Add(BuildToggle("RAM", c => c.Metrics.Ram, (c, value) => c.Metrics.Ram = value));
        metrics.DropDownItems.Add(BuildToggle("Network", c => c.Metrics.Network, (c, value) => c.Metrics.Network = value));
        metrics.DropDownItems.Add(BuildToggle("Disk", c => c.Metrics.Disk, (c, value) => c.Metrics.Disk = value));
        metrics.DropDownItems.Add(BuildToggle("GPU", c => c.Metrics.Gpu, (c, value) => c.Metrics.Gpu = value));
        metrics.DropDownItems.Add(BuildToggle("Temperature", c => c.Metrics.Temperature, (c, value) => c.Metrics.Temperature = value));
        return metrics;
    }

    private static FormsToolStripMenuItem BuildAgentsMenu()
    {
        var agents = new FormsToolStripMenuItem("Agents");
        agents.DropDownItems.Add(BuildToggle("CommandCode", c => c.Agents.CommandCode, (c, value) => c.Agents.CommandCode = value));
        agents.DropDownItems.Add(BuildToggle("OpenCode", c => c.Agents.OpenCode, (c, value) => c.Agents.OpenCode = value));
        agents.DropDownItems.Add(BuildToggle("ChatGPT Usage", c => c.Agents.Codex, (c, value) => c.Agents.Codex = value));
        agents.DropDownItems.Add(BuildToggle("Antigravity", c => c.Agents.Antigravity, (c, value) => c.Agents.Antigravity = value));
        agents.DropDownItems.Add(BuildToggle("Claude", c => c.Agents.Claude, (c, value) => c.Agents.Claude = value));
        return agents;
    }

    private static FormsToolStripMenuItem BuildChoiceMenu(
        string title,
        IReadOnlyList<string> values,
        Func<string> get,
        Action<string> set)
    {
        var parent = new FormsToolStripMenuItem(title);
        foreach (string value in values)
        {
            var item = new FormsToolStripMenuItem(value) { Checked = string.Equals(get(), value, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) =>
            {
                set(value);
                ConfigManager.Instance.Save();
                foreach (var sibling in parent.DropDownItems.OfType<FormsToolStripMenuItem>())
                {
                    sibling.Checked = ReferenceEquals(sibling, item);
                }
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private static FormsToolStripMenuItem BuildToggle(
        string title,
        Func<ConfigData, bool> get,
        Action<ConfigData, bool> set)
    {
        var config = ConfigManager.Instance.Config;
        var item = new FormsToolStripMenuItem(title) { Checked = get(config), CheckOnClick = false };
        item.Click += (_, _) =>
        {
            var current = ConfigManager.Instance.Config;
            set(current, !get(current));
            ConfigManager.Instance.Save();
            item.Checked = get(current);
        };
        return item;
    }

    private static FormsToolStripMenuItem CreateAction(string title, Action action)
    {
        var item = new FormsToolStripMenuItem(title);
        item.Click += (_, _) => action();
        return item;
    }

    private static void OpenConfig()
    {
        try
        {
            string path = ConfigManager.Instance.ConfigPath;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // The menu remains usable if the default file handler is unavailable.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
