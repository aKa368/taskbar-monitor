namespace TaskbarMonitor.UI.Layout;

/// <summary>
/// Layout presets for the taskbar widget.
/// Compact   — pods ngang: metrics + agent usage text (mặc định)
/// Minimal   — chỉ text, không border/chart — nhẹ nhất
/// TwoLine   — 2 hàng gọn: metrics trên, agents dưới (vừa taskbar 48px)
/// AgentCentric — chỉ agent usage bars 5h/7d
/// </summary>
public enum WidgetLayoutKind
{
    Compact,
    Minimal,
    TwoLine,
    AgentCentric,
    Grid
}
