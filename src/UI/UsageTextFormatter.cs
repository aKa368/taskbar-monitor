namespace TaskbarMonitor.UI;

/// <summary>Formats text-only agent usage details used by the taskbar widget.</summary>
public static class UsageTextFormatter
{
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