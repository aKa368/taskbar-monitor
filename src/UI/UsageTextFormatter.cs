namespace TaskbarMonitor.UI;

using TaskbarMonitor.AgentUsage;

/// <summary>Formats text-only agent usage details used by the taskbar widget.</summary>
public static class UsageTextFormatter
{
    public static string FormatAgentDisplay(UsageData? data, bool showReset, bool diagnosticFailed = false)
    {
        if (data is null) return diagnosticFailed ? "ERR" : "--";
        string? value = null;
        if (data.UsedPercent5h.HasValue || data.UsedPercent7d.HasValue)
            value = FormatRemainingQuota(data.UsedPercent5h, data.UsedPercent7d);
        else if (data.Last5h is { } totals)
            value = $"${totals.Cost ?? 0:F2} · {(totals.TokensTotal ?? 0) / 1e6:F1}M";

        if (data.Error is not null) return value is null ? "ERR" : $"{value} · ERR";
        return value ?? "N/A";
    }

    public static string FormatRemainingQuota(double? used5h, double? used7d)
    {
        var parts = new List<string>(2);
        if (used5h.HasValue) parts.Add($"5h {100 - Math.Clamp(used5h.Value, 0, 100):F0}% left");
        if (used7d.HasValue) parts.Add($"7d {100 - Math.Clamp(used7d.Value, 0, 100):F0}% left");
        return string.Join(" · ", parts);
    }

    public static string FormatCompactAgentDisplay(UsageData? data, bool diagnosticFailed = false)
    {
        if (data is null) return diagnosticFailed ? "ERR" : "--";
        var parts = new List<string>(2);
        if (data.UsedPercent5h.HasValue) parts.Add($"5h{100 - Math.Clamp(data.UsedPercent5h.Value, 0, 100):F0}");
        if (data.UsedPercent7d.HasValue) parts.Add($"7d{100 - Math.Clamp(data.UsedPercent7d.Value, 0, 100):F0}");
        string? value = parts.Count > 0 ? string.Join(" · ", parts) : null;
        if (value is null && data.Last5h is { } totals)
            value = $"${totals.Cost ?? 0:F2} · {(totals.TokensTotal ?? 0) / 1e6:F1}M";
        if (data.Error is not null) return value is null ? "ERR" : $"{value} · ERR";
        return value ?? "N/A";
    }

    public readonly record struct QuotaWindow(double? UsedPercent, DateTime? ResetsAt, string? Label)
    {
        public bool IsAvailable => UsedPercent.HasValue;
    }

    public static QuotaWindow SelectQuotaWindow(double? fiveHourPercent, DateTime? fiveHourReset,
        double? sevenDayPercent, DateTime? sevenDayReset) =>
        fiveHourPercent.HasValue ? new(fiveHourPercent, fiveHourReset, "5h")
        : sevenDayPercent.HasValue ? new(sevenDayPercent, sevenDayReset, "7d")
        : new(null, null, null);

    public static string FormatBestQuota(double? fiveHourPercent, DateTime? fiveHourReset,
        double? sevenDayPercent, DateTime? sevenDayReset, bool showReset)
    {
        QuotaWindow selected = SelectQuotaWindow(fiveHourPercent, fiveHourReset, sevenDayPercent, sevenDayReset);
        if (!selected.IsAvailable) return "N/A";
        string windowLabel = selected.Label == "7d" ? " 7d" : string.Empty;
        string resetText = showReset ? $" · {FormatResetTime(selected.ResetsAt)}" : string.Empty;
        return $"{selected.UsedPercent!.Value:F0}%{windowLabel}{resetText}";
    }

    public static string FormatQuotaPercent(double? usedPercent, bool showReset, DateTime? resetsAt)
    {
        if (!usedPercent.HasValue) return "--";
        string resetText = showReset ? $" · {FormatResetTime(resetsAt)}" : string.Empty;
        return $"{usedPercent.Value:F0}%{resetText}";
    }

    public static string FormatResetTime(DateTime? resetsAt)
    {
        if (!resetsAt.HasValue) return string.Empty;

        var remaining = resetsAt.Value - DateTime.Now;
        if (remaining <= TimeSpan.Zero) return "resetting";

        int totalHours = (int)remaining.TotalHours;
        return $"reset in {totalHours:D2}:{remaining.Minutes:D2}";
    }
}
