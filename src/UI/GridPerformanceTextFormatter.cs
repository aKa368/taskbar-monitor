namespace TaskbarMonitor.UI;

public static class GridPerformanceTextFormatter
{
    public static string Format(bool gpuEnabled, double gpuPercent, bool temperatureEnabled,
        double gpuCelsius)
    {
        var parts = new List<string>(3);
        if (gpuEnabled)
        {
            parts.Add(double.IsFinite(gpuPercent) ? $"GPU {gpuPercent:F0}%" : "GPU --");
            if (temperatureEnabled)
                parts.Add(double.IsFinite(gpuCelsius) ? $"{gpuCelsius:F0} C" : "-- C");
        }
        return string.Join("  -  ", parts);
    }
}
