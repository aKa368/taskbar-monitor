using Deskband11Lib.Core;
using Deskband11Lib.Wpf;
using TaskbarMonitor.Config;

namespace TaskbarMonitor;

internal static class TaskbarHostOptions
{
    internal static double AccountWidth(ConfigData config)
    {
        double fontFactor = Math.Max(1.0, (config.FontSize > 0 ? config.FontSize : 11.5) / 11.5);
        double baseWidth = 340; // CPA baseline: "[1 Acc] 5h XX% (XXm) · 7d XX%"

        int agentCount = CountEnabledAgents(config);
        if (agentCount > 1)
        {
            baseWidth += (agentCount - 1) * 110;
        }

        baseWidth += 30; // Safety padding & margins
        if (config.PreferredWidth > baseWidth) baseWidth = config.PreferredWidth;
        return Math.Ceiling(baseWidth * fontFactor);
    }

    internal static double SystemWidth(ConfigData config)
    {
        double fontFactor = Math.Max(1.0, (config.FontSize > 0 ? config.FontSize : 11.5) / 11.5);
        double baseWidth = 340; // APU (or CPU+GPU) + RAM + Network baseline

        if (!TaskbarMonitor.Metrics.HardwareTopology.IsSharedApu && config.Metrics.Cpu && config.Metrics.Gpu)
            baseWidth += 60; // Discrete GPU adds a second temperature/usage pod
        if (config.Metrics.Disk)
            baseWidth += 60;
        if (config.Metrics.GameOverlay)
            baseWidth += 130; // FPS and PING pods

        baseWidth += 30; // Safety padding & margins
        if (config.PreferredWidth > baseWidth) baseWidth = config.PreferredWidth;
        return Math.Ceiling(baseWidth * fontFactor);
    }

    internal static int CountEnabledAgents(ConfigData config) =>
        (config.Agents.CommandCode ? 1 : 0) + (config.Agents.OpenCode ? 1 : 0)
        + (config.Agents.Antigravity ? 1 : 0)
        + (config.Agents.Claude ? 1 : 0);

    internal static TaskbarContentPlacement AccountPlacement(ConfigData config)
    {
        bool isCenter = string.Equals(config.Position, "Center", StringComparison.OrdinalIgnoreCase);
        return isCenter ? TaskbarContentPlacement.BeforeStartButton : TaskbarContentPlacement.AccountArea;
    }

    internal static TaskbarContentPlacement SystemPlacement(ConfigData config)
    {
        bool isCenter = string.Equals(config.Position, "Center", StringComparison.OrdinalIgnoreCase);
        return isCenter ? TaskbarContentPlacement.AfterTaskbarButtons : TaskbarContentPlacement.BeforeNotificationArea;
    }

    internal static TaskbarContentHostOptions CreateAccounts(ConfigData config)
        => Create(AccountWidth(config), AccountPlacement(config));

    internal static TaskbarContentHostOptions CreateSystem(ConfigData config)
        => Create(SystemWidth(config), SystemPlacement(config));

    private static TaskbarContentHostOptions Create(double width, TaskbarContentPlacement placement) => new()
    {
        PreferredWidth = width,
        PreferredHeight = 48,
        Placement = placement,
        TrackTaskbarButtons = true,
        TrackNotificationArea = true,
        AllowFixedSlotResize = true,
        AnimateLayoutChanges = false,
        LayoutRefreshInterval = TimeSpan.FromSeconds(5)
    };
}
