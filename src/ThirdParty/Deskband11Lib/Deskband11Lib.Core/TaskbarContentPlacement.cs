namespace Deskband11Lib.Core;

public enum TaskbarContentPlacement
{
    Auto,
    LeftEdge,
    BeforeNotificationArea,
    BeforeStartButton,
    /// <summary>
    /// A dedicated account-status area. It occupies the far-left safe gap on a
    /// centered taskbar and the first safe slot after the taskbar button group
    /// on a left-aligned taskbar.
    /// </summary>
    AccountArea
}
