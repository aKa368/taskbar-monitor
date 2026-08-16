using System.Timers;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Thread-safe cache for comparatively expensive native metric readers. The
/// sampler may tick more often than a reader needs; each reader controls its own
/// throttling while this class prevents overlapping sampling passes.
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

    public MetricRingBuffer(int capacity = 60)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _values = new double[capacity];
    }

    public void AddNextChartValue(double value)
    {
        lock (_sync)
        {
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            _count = Math.Min(_count + 1, _values.Length);
        }
    }

    public double Latest()
    {
        lock (_sync)
            return _count == 0 ? double.NaN : _values[(_next - 1 + _values.Length) % _values.Length];
    }

    public IReadOnlyList<double> Snapshot()
    {
        lock (_sync)
        {
            var result = new double[_count];
            int start = (_next - _count + _values.Length) % _values.Length;
            for (int i = 0; i < _count; i++)
                result[i] = _values[(start + i) % _values.Length];
            return result;
        }
    }
}

/// <summary>Samples independent metrics without allowing timer callbacks to overlap.</summary>
public sealed class MetricSampler : IDisposable
{
    private readonly System.Timers.Timer? _timer;
    private IDisposable? _sharedSubscription;
    private readonly IReadOnlyDictionary<string, Func<double>> _readers;
    private readonly Dictionary<string, MetricRingBuffer> _histories;
    private int _sampling;

    public MetricSampler(IReadOnlyDictionary<string, Func<double>> readers, double intervalMilliseconds = 1000, int historyCapacity = 60, bool useSharedClock = false)
    {
        _readers = readers ?? throw new ArgumentNullException(nameof(readers));
        _histories = readers.Keys.ToDictionary(k => k, _ => new MetricRingBuffer(historyCapacity));
        if (useSharedClock)
            _sharedSubscription = SharedMetricSamplerClock.Subscribe(SampleNow);
        else
        {
            _timer = new System.Timers.Timer(intervalMilliseconds) { AutoReset = true };
            _timer.Elapsed += OnElapsed;
        }
    }

    public bool IsRunning => _sharedSubscription is not null || (_timer?.Enabled ?? false);
    public void Start() => _timer?.Start();
    public void Stop()
    {
        _timer?.Stop();
        _sharedSubscription?.Dispose();
        _sharedSubscription = null;
    }
    public IReadOnlyList<double> GetHistory(string metric) => _histories[metric].Snapshot();
    public double GetLatest(string metric) => _histories[metric].Latest();

    public void SampleNow() => SampleNowCore(predicate: null);

    public void SampleNow(Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        SampleNowCore(predicate);
    }

    public void AddNextChartValue(string metric, double value) => _histories[metric].AddNextChartValue(value);

    private void SampleNowCore(Func<string, bool>? predicate)
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0)
            return;

        try
        {
            foreach (var pair in _readers)
            {
                if (predicate is not null && !predicate(pair.Key))
                    continue;

                double value;
                try { value = pair.Value(); }
                catch { value = double.NaN; }
                _histories[pair.Key].AddNextChartValue(value);
            }
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    private void OnElapsed(object? sender, ElapsedEventArgs e) => SampleNow();

    public void Dispose()
    {
        Stop();
        if (_timer is not null)
        {
            _timer.Elapsed -= OnElapsed;
            _timer.Dispose();
        }
    }
}
