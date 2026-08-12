using System.Diagnostics;

namespace TaskbarMonitor.Metrics;

public readonly record struct DiskMetrics(string Disk, double UsagePercent, double ReadBytesPerSecond, double WriteBytesPerSecond);

/// <summary>Reads per-disk physical disk performance counters. Missing categories yield an empty result.</summary>
public sealed class DiskMonitor : IDisposable
{
    private readonly List<(string Name, PerformanceCounter Usage, PerformanceCounter Read, PerformanceCounter Write)> _counters = [];

    public DiskMonitor()
    {
        try
        {
            foreach (string name in new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames().Where(n => n != "_Total"))
                _counters.Add((name, New("% Disk Time", name), New("Disk Read Bytes/sec", name), New("Disk Write Bytes/sec", name)));
        }
        catch { Dispose(); }
    }

    public IReadOnlyList<DiskMetrics> Sample() => _counters.Select(c => new DiskMetrics(c.Name,
        Math.Clamp(Read(c.Usage), 0, 100), Math.Max(0, Read(c.Read)), Math.Max(0, Read(c.Write)))).ToArray();

    public void Dispose() { foreach (var c in _counters) { c.Usage.Dispose(); c.Read.Dispose(); c.Write.Dispose(); } _counters.Clear(); }
    private static PerformanceCounter New(string counter, string instance) => new("PhysicalDisk", counter, instance, true);
    private static float Read(PerformanceCounter counter) { try { return counter.NextValue(); } catch { return 0; } }
}
