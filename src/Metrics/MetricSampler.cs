using System.Timers;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Thread-safe cache for a comparatively expensive native metric reader. The
/// sampler may tick every second, but WMI and hardware sensor scans do not need
/// to run at that cadence. The first read is immediate; later reads reuse the
/// last value until the interval elapses.
/// </summary>
public sealed class ThrottledMetricReader
{
    private readonly Func<double> _reader;
    private readonly TimeSpan _minimumInterval;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private DateTimeOffset _nextReadAt = DateTimeOffset.MinValue;
    private double _lastValue = double.NaN;

    public ThrottledMetricReader(Func<double> reader, TimeSpan minimumInterval, TimeProvider? timeProvider = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (minimumInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        _minimumInterval = minimumInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public double Read()
    {
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (now < _nextReadAt) return _lastValue;
            try { _lastValue = _reader(); }
            catch { _lastValue = double.NaN; }
            _nextReadAt = now + _minimumInterval;
            return _lastValue;
        }
    }
}

public sealed class MetricRingBuffer
{
    private readonly double[] _values;
    private int _next;
    private int _count;
    private readonly object _sync = new();
    public MetricRingBuffer(int capacity = 60) { if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity)); _values = new double[capacity]; }
    public void AddNextChartValue(double value) { lock (_sync) { _values[_next] = value; _next = (_next + 1) % _values.Length; _count = Math.Min(_count + 1, _values.Length); } }
    public IReadOnlyList<double> Snapshot() { lock (_sync) { var result = new double[_count]; int start = (_next - _count + _values.Length) % _values.Length; for (int i = 0; i < _count; i++) result[i] = _values[(start + i) % _values.Length]; return result; } }
}

/// <summary>Samples independent metrics every second; each metric has its own lock and history.</summary>
public sealed class MetricSampler : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly IReadOnlyDictionary<string, Func<double>> _readers;
    private readonly Dictionary<string, object> _locks;
    private readonly Dictionary<string, MetricRingBuffer> _histories;
    public MetricSampler(IReadOnlyDictionary<string, Func<double>> readers, double intervalMilliseconds = 1000, int historyCapacity = 60)
    {
        _readers = readers ?? throw new ArgumentNullException(nameof(readers));
        _locks = readers.Keys.ToDictionary(k => k, _ => new object());
        _histories = readers.Keys.ToDictionary(k => k, _ => new MetricRingBuffer(historyCapacity));
        _timer = new System.Timers.Timer(intervalMilliseconds) { AutoReset = true };
        _timer.Elapsed += OnElapsed;
    }
    public bool IsRunning => _timer.Enabled;
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public IReadOnlyList<double> GetHistory(string metric) => _histories[metric].Snapshot();
    public void SampleNow()
    {
        foreach (var pair in _readers)
            lock (_locks[pair.Key]) { try { _histories[pair.Key].AddNextChartValue(pair.Value()); } catch { _histories[pair.Key].AddNextChartValue(double.NaN); } }
    }
    public void AddNextChartValue(string metric, double value) { lock (_locks[metric]) _histories[metric].AddNextChartValue(value); }
    private void OnElapsed(object? sender, ElapsedEventArgs e) => SampleNow();
    public void Dispose() { _timer.Stop(); _timer.Elapsed -= OnElapsed; _timer.Dispose(); }
}
