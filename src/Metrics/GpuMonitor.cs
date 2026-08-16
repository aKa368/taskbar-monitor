using System.Management;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Reads GPU engine utilization without keeping one native counter
/// handle alive for every process/engine instance. Windows can expose thousands
/// of those instances on a busy desktop. Task Manager's overall GPU percentage
/// is the busiest physical engine, not the sum of every engine.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private readonly Func<IEnumerable<(string Instance, double Utilization)>> _readSamples;
    private double _lastFiniteSample = double.NaN;
    private bool _heldTransientGap;

    public GpuMonitor() : this(ReadFormattedSamples) { }

    internal GpuMonitor(Func<IEnumerable<(string Instance, double Utilization)>> readSamples) =>
        _readSamples = readSamples ?? throw new ArgumentNullException(nameof(readSamples));

    public double Sample()
    {
        try
        {
            double current = AggregateBusiestEngine(_readSamples());
            if (!double.IsFinite(current))
                return PreserveThroughTransientGap();

            // WMI's formatted GPU provider can occasionally publish an all-zero
            // snapshot while the engine counters are being refreshed. Keep the
            // last real reading for one poll so a brief telemetry gap cannot
            // overwrite a visibly busy GPU with a false 0%.
            if (current <= 0 && double.IsFinite(_lastFiniteSample) && _lastFiniteSample > 0 && !_heldTransientGap)
            {
                _heldTransientGap = true;
                return _lastFiniteSample;
            }

            _lastFiniteSample = current;
            _heldTransientGap = false;
            return current;
        }
        catch (Exception ex)
        {
            Diagnostics.ReportReaderFailure("gpu.sample", ex);
            return PreserveThroughTransientGap();
        }
    }

    private double PreserveThroughTransientGap()
    {
        if (double.IsFinite(_lastFiniteSample) && !_heldTransientGap)
        {
            _heldTransientGap = true;
            return _lastFiniteSample;
        }
        return double.NaN;
    }

    internal static double AggregateBusiestEngine(IEnumerable<(string Instance, double Utilization)> samples)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, utilization) in samples)
        {
            if (!double.IsFinite(utilization) || utilization < 0) continue;
            string engine = GetPhysicalEngineKey(instance);
            engines[engine] = engines.GetValueOrDefault(engine) + utilization;
        }

        return engines.Count == 0 ? double.NaN : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    private static string GetPhysicalEngineKey(string instance)
    {
        // Instance names are normally:
        // pid_123_luid_0x..._phys_0_eng_3_engtype_3D.
        // Excluding pid combines clients which share the same physical engine.
        int luid = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        int pid = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (luid >= 0) return instance[luid..];
        return pid >= 0 ? instance[(instance.IndexOf('_', pid + 4) + 1)..] : instance;
    }

    private static IEnumerable<(string Instance, double Utilization)> ReadFormattedSamples()
    {
        // The formatted provider cooks timer/rate counters for us. A raw
        // PerformanceCounterCategory snapshot cannot correctly calculate a
        // percentage without retaining a prior sample.
        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject result in results)
        {
            using (result)
            {
                string? instance = result["Name"] as string;
                if (instance is null) continue;
                if (TryConvertUtilization(result["UtilizationPercentage"], out double utilization))
                    yield return (instance, utilization);
            }
        }
    }

    private static bool TryConvertUtilization(object? value, out double utilization)
    {
        try
        {
            utilization = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return double.IsFinite(utilization);
        }
        catch (Exception) when (value is null || value is string || value is IConvertible)
        {
            utilization = double.NaN;
            return false;
        }
    }

    public void Dispose() { }
}
