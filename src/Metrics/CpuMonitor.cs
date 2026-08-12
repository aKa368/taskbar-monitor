using System.Diagnostics;

namespace TaskbarMonitor.Metrics;

/// <summary>Reads total processor utilization and frequency from Windows performance counters.</summary>
public sealed class CpuMonitor : IDisposable
{
    private readonly Func<float> _usageReader;
    private readonly Func<float> _frequencyReader;
    private readonly IDisposable? _resources;

    public CpuMonitor()
    {
        PerformanceCounter? usage = TryCreate("Processor Information", "% Processor Time", "_Total");
        PerformanceCounter? frequency = TryCreate("Processor Information", "Processor Frequency", "_Total");
        _usageReader = () => SafeNextValue(usage);
        _frequencyReader = () => SafeNextValue(frequency);
        _resources = new CounterResources(usage, frequency);
    }

    public CpuMonitor(Func<float> usageReader, Func<float>? frequencyReader = null)
    {
        _usageReader = usageReader ?? throw new ArgumentNullException(nameof(usageReader));
        _frequencyReader = frequencyReader ?? (() => 0);
    }

    public CpuMetrics Sample() => new(ClampPercent(SafeRead(_usageReader)), Math.Max(0, SafeRead(_frequencyReader)));

    public static double ClampPercent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    public void Dispose() => _resources?.Dispose();

    private static float SafeRead(Func<float> reader) { try { return reader(); } catch { return 0; } }
    private static float SafeNextValue(PerformanceCounter? counter) { try { return counter?.NextValue() ?? 0; } catch { return 0; } }
    private static PerformanceCounter? TryCreate(string category, string counter, string instance)
    {
        try { return new PerformanceCounter(category, counter, instance, readOnly: true); }
        catch { return null; }
    }

    private sealed class CounterResources(params PerformanceCounter?[] counters) : IDisposable
    {
        public void Dispose() { foreach (var counter in counters) counter?.Dispose(); }
    }
}

public readonly record struct CpuMetrics(double UsagePercent, double FrequencyMhz);
