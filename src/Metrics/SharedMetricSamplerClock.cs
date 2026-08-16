using System.Threading;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Process-wide sampler pulse. MetricSampler instances subscribe and are removed
/// on Stop/Dispose, so the timer does not retain closed view-models.
/// </summary>
internal static class SharedMetricSamplerClock
{
    private static readonly object Sync = new();
    private static readonly Dictionary<int, Action> Subscribers = [];
    private static Timer? _timer;
    private static int _nextId;

    public static IDisposable Subscribe(Action sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (Sync)
        {
            if (_timer is null)
                _timer = new Timer(static _ => Pulse(), null, TimeSpan.FromMilliseconds(2500), TimeSpan.FromMilliseconds(2500));

            int id = ++_nextId;
            Subscribers.Add(id, sample);
            return new Subscription(id);
        }
    }

    private static void Pulse()
    {
        Action[] snapshot;
        lock (Sync)
            snapshot = [.. Subscribers.Values];

        foreach (Action sample in snapshot)
        {
            try { sample(); }
            catch { }
        }
    }

    private static void Remove(int id)
    {
        lock (Sync)
        {
            Subscribers.Remove(id);
            if (Subscribers.Count == 0 && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }
    }

    private sealed class Subscription(int id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Remove(id);
        }
    }
}
