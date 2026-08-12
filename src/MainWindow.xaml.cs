using System;
using System.Threading.Tasks;
using System.Windows;
using Deskband11Lib.Core;
using Deskband11Lib.Wpf;
using TaskbarMonitor.Config;

namespace TaskbarMonitor;

public partial class MainWindow : Window
{
    private readonly TaskbarContentHostOptions _hostOptions;
    private bool _isClosing;

    public TaskbarContentHost TaskbarContentHost { get; }

    public MainWindow()
    {
        InitializeComponent();

        _hostOptions = CreateHostOptions(ConfigManager.Instance.Config);
        TaskbarContentHost = new TaskbarContentHost(this, (FrameworkElement)Content, _hostOptions);
        ConfigManager.Instance.ConfigReloaded += OnConfigReloaded;
    }

    /// <summary>
    /// Waits for Explorer's taskbar layout, then converts this real WPF Window
    /// into the taskbar child window. The host library owns HWND parenting,
    /// clipping, UI Automation measurement, and Explorer restart recovery.
    /// </summary>
    public Task PrepareTaskbarContentAsync() => TaskbarContentHost.AttachWhenLayoutReadyAsync();

    private void OnConfigReloaded(object? sender, ConfigData config)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _hostOptions.PreferredWidth = Math.Max(80, config.PreferredWidth);
            _hostOptions.Placement = ParsePosition(config.Position, config.Placement);
            TaskbarContentHost.RefreshLayout();
        });
    }

    private static TaskbarContentHostOptions CreateHostOptions(ConfigData config) => new()
    {
        PreferredWidth = Math.Max(80, config.PreferredWidth),
        PreferredHeight = 48,
        Placement = ParsePosition(config.Position, config.Placement),
        TrackTaskbarButtons = true,
        TrackNotificationArea = true,
        AllowFixedSlotResize = true,
        AnimateLayoutChanges = false,
        // Explorer layout almost never changes while the taskbar is idle. A 5s
        // safety refresh keeps reattachment/resizing resilient without a 2 Hz
        // host-layout polling cost.
        LayoutRefreshInterval = TimeSpan.FromSeconds(5)
    };

    private static TaskbarContentPlacement ParsePosition(string? position, string? legacyPlacement)
    {
        return position?.ToLowerInvariant() switch
        {
            "left" => TaskbarContentPlacement.LeftEdge,
            "right" => TaskbarContentPlacement.BeforeNotificationArea,
            "center" => TaskbarContentPlacement.Auto,
            _ when Enum.TryParse<TaskbarContentPlacement>(legacyPlacement, true, out var legacy) => legacy,
            _ => TaskbarContentPlacement.Auto
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_isClosing)
        {
            _isClosing = true;
            ConfigManager.Instance.ConfigReloaded -= OnConfigReloaded;
            TaskbarContentHost.Dispose();
            TaskbarControl.Dispose();
        }

        base.OnClosed(e);
    }
}