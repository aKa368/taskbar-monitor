namespace TaskbarMonitor.UI.Layout;

/// <summary>
/// Metadata cho mỗi layout preset — quyết định pod nào hiển thị,
/// có border hay không, 1 hay 2 hàng, có text reset countdown hay không.
/// </summary>
public sealed record LayoutInfo(
    string Name,
    string Description,
    bool ShowMetricPods,
    bool ShowAgentPods,
    bool ShowPodBorders,
    bool TwoRow,
    bool ShowAgentResetText,
    double MinTaskbarHeight);

public static class LayoutDefinition
{
    public static LayoutInfo Get(WidgetLayoutKind kind) => kind switch
    {
        WidgetLayoutKind.Compact => new LayoutInfo(
            "Compact", "Pods ngang: metrics + agent usage, chỉ text", true, true, true, false, true, 32),
        WidgetLayoutKind.Minimal => new LayoutInfo(
            "Minimal", "Chỉ text, không border/chart — nhẹ nhất", true, true, false, false, false, 28),
        WidgetLayoutKind.TwoLine => new LayoutInfo(
            "TwoLine", "2 hàng gọn: metrics trên, agents dưới", true, true, true, true, false, 40),
        WidgetLayoutKind.AgentCentric => new LayoutInfo(
            "AgentCentric", "Chỉ agent usage text", false, true, true, false, true, 32),
        WidgetLayoutKind.Grid => new LayoutInfo(
            "Grid", "Metrics ghép cặp + usage agents, chỉ text", true, true, true, true, false, 40),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
