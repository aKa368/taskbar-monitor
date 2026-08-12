using System.Diagnostics;

namespace TaskbarMonitor.Metrics;

/// <summary>Optionally sums GPU engine utilization counters and caps aggregate utilization at 100%.</summary>
public sealed class GpuMonitor : IDisposable
{
    private readonly List<PerformanceCounter> _counters = [];
    public GpuMonitor()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (string instance in category.GetInstanceNames())
                _counters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true));
        }
        catch (Exception ex)
        {
            Diagnostics.ReportReaderFailure("gpu.init", ex);
            Dispose();
        }
    }
    public double Sample()
    {
        double total = 0;
        foreach (var counter in _counters)
        {
            try { total += counter.NextValue(); }
            catch (Exception ex) { Diagnostics.ReportReaderFailure("gpu.sample", ex); }
        }
        return double.IsFinite(total) ? Math.Clamp(total, 0, 100) : 0;
    }
    public void Dispose() { foreach (var counter in _counters) counter.Dispose(); _counters.Clear(); }
}
