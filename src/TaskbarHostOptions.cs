using Deskband11Lib.Core;
using Deskband11Lib.Wpf;
using TaskbarMonitor.Config;

namespace TaskbarMonitor;

internal static class TaskbarHostOptions
{
    // Keep the account widget close to the system widget width. A small extra
    // allowance accommodates the provider label/value pair without creating a
    // visually oversized block on the taskbar.
    internal static double AccountWidth(ConfigData config)
        => SystemWidth(config) + 16;

    internal static double SystemWidth(ConfigData config)
        => Math.Max(280, config.PreferredWidth * 0.57);

    internal static int CountEnabledAgents(ConfigData config) =>
        (config.Agents.CommandCode ? 1 : 0) + (config.Agents.OpenCode ? 1 : 0)
        + (config.Agents.Codex ? 1 : 0) + (config.Agents.Antigravity ? 1 : 0)
        + (config.Agents.Claude ? 1 : 0);

    internal static TaskbarContentHostOptions CreateAccounts(ConfigData config)
        => Create(AccountWidth(config), TaskbarContentPlacement.AccountArea);

    internal static TaskbarContentHostOptions CreateSystem(ConfigData config)
        => Create(SystemWidth(config), TaskbarContentPlacement.BeforeNotificationArea);

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
