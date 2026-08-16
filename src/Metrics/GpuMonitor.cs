using System.Diagnostics;
using System.Globalization;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Reads overall GPU utilization from persistent Windows GPU Engine counters.
/// PerformanceCounter is backed by PDH and the query handles are kept alive;
/// engine discovery is refreshed periodically instead of being repeated per UI tick.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private readonly Func<IEnumerable<(string Instance, double Utilization)>>? _readSamples;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private List<GpuCounter> _counters = [];
    private long _nextRefreshTimestamp;
    private double _lastFiniteSample = double.NaN;
    private bool _heldTransientGap;
    private bool _disposed;

    public GpuMonitor(TimeProvider? timeProvider = null, TimeSpan? refreshInterval = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(15);
        RefreshCounters(force: true);
    }

    public GpuMonitor(Func<IEnumerable<(string Instance, double Utilization)>> readSamples)
    {
        _readSamples = readSamples ?? throw new ArgumentNullException(nameof(readSamples));
        _timeProvider = TimeProvider.System;
        _refreshInterval = TimeSpan.FromSeconds(15);
    }

    public double Sample()
    {
        lock (_sync)
        {
            if (_disposed) return double.NaN;

            IEnumerable<(string Instance, double Utilization)> samples;
            try
            {
                if (_readSamples is not null)
                {
                    samples = _readSamples();
                }
                else
                {
                    RefreshCounters(force: false);
                    samples = ReadCounterSamples();
                }

                double value = AggregateBusiestEngine(samples);
                if (!double.IsFinite(value)) return PreserveThroughTransientGap();

                // WDDM can publish one all-zero snapshot while the engine list is
                // being refreshed. Hold the last valid busy value once, then accept
                // a sustained real 0% on the next sample.
                if (value <= 0 && _lastFiniteSample > 0 && !_heldTransientGap)
                {
                    _heldTransientGap = true;
                    return _lastFiniteSample;
                }

                _lastFiniteSample = value;
                _heldTransientGap = false;
                return value;
            }
            catch (Exception ex)
            {
                Diagnostics.ReportReaderFailure("gpu.sample", ex);
                return PreserveThroughTransientGap();
            }
        }
    }

    public static double AggregateBusiestEngine(IEnumerable<(string Instance, double Utilization)> samples)
    {
        var engineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample.Utilization)) continue;
            double utilization = Math.Clamp(sample.Utilization, 0, 100);
            string engineKey = PhysicalEngineKey(sample.Instance);
            engineTotals[engineKey] = Math.Min(100, engineTotals.GetValueOrDefault(engineKey) + utilization);
        }

        return engineTotals.Count == 0 ? double.NaN : engineTotals.Values.Max();
    }

    public static string PhysicalEngineKey(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) return string.Empty;
        int luidStart = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        return luidStart >= 0 ? instance[luidStart..] : instance;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeCounters(_counters);
            _counters = [];
        }
    }

    private IEnumerable<(string Instance, double Utilization)> ReadCounterSamples()
    {
        foreach (GpuCounter counter in _counters)
        {
            double value;
            try { value = counter.Counter.NextValue(); }
            catch (Exception ex)
            {
                Diagnostics.ReportReaderFailure("gpu.counter", ex);
                value = double.NaN;
            }
            yield return (counter.Instance, value);
        }
    }

    private void RefreshCounters(bool force)
    {
        long now = _timeProvider.GetTimestamp();
        if (!force && now < Volatile.Read(ref _nextRefreshTimestamp)) return;

        List<GpuCounter> discovered = [];
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (string instance in category.GetInstanceNames())
            {
                if (string.IsNullOrWhiteSpace(instance)) continue;
                try
                {
                    var counter = new PerformanceCounter(
                        category.CategoryName,
                        "Utilization Percentage",
                        instance,
                        readOnly: true);
                    discovered.Add(new GpuCounter(instance, counter));
                }
                catch (Exception ex)
                {
                    Diagnostics.ReportReaderFailure("gpu.counter.init", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.ReportReaderFailure("gpu.discovery", ex);
        }

        if (discovered.Count > 0 || _counters.Count == 0)
        {
            List<GpuCounter> previous = _counters;
            _counters = discovered;
            DisposeCounters(previous);
        }
        else
        {
            DisposeCounters(discovered);
        }

        long retryTicks = _counters.Count > 0
            ? (long)(_refreshInterval.TotalSeconds * Stopwatch.Frequency)
            : 5 * Stopwatch.Frequency;
        Volatile.Write(ref _nextRefreshTimestamp, now + Math.Max(1, retryTicks));
    }

    private static void DisposeCounters(IEnumerable<GpuCounter> counters)
    {
        foreach (GpuCounter counter in counters)
        {
            try { counter.Counter.Dispose(); }
            catch { }
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

    private sealed record GpuCounter(string Instance, PerformanceCounter Counter);
}
