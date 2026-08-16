using System.Diagnostics;
using System.Windows.Threading;

namespace TaskbarMonitor.UI;

/// <summary>
/// One dispatcher timer services all taskbar content hosts. Subscriptions are
/// removed on Dispose so the hub cannot retain closed host view-models.
/// </summary>
internal sealed class SharedTaskbarTickSource : IDisposable
{
    private static readonly object Sync = new();
    private static readonly Dictionary<int, SharedTaskbarTickSource> Sources = [];
    private static int _nextId;
    private static DispatcherTimer? _timer;

    private readonly Action _callback;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _id;
    private TimeSpan _interval;
    private long _nextTick;
    private bool _disposed;

    private SharedTaskbarTickSource(int id, Action callback, TimeSpan interval)
    {
        _id = id;
        _callback = callback;
        _interval = interval;
        _nextTick = _clock.ElapsedTicks + ToClockTicks(interval);
    }

    public static SharedTaskbarTickSource Subscribe(Action callback, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        lock (Sync)
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _timer.Tick += OnHubTick;
                _timer.Start();
            }

            var source = new SharedTaskbarTickSource(++_nextId, callback, interval);
            Sources.Add(source._id, source);
            return source;
        }
    }

    public void Start() { }

    public void Stop() => Dispose();

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value));
            _interval = value;
            _nextTick = _clock.ElapsedTicks + ToClockTicks(value);
        }
    }

    private static void OnHubTick(object? sender, EventArgs e)
    {
        SharedTaskbarTickSource[] snapshot;
        lock (Sync)
            snapshot = [.. Sources.Values];

        foreach (SharedTaskbarTickSource source in snapshot)
            source.Pulse();
    }

    private void Pulse()
    {
        if (_disposed || _clock.ElapsedTicks < _nextTick)
            return;

        _nextTick = _clock.ElapsedTicks + ToClockTicks(_interval);
        try { _callback(); }
        catch (Exception ex) { Diagnostics.ReportReaderFailure("ui.shared-tick", ex); }
    }

    private static long ToClockTicks(TimeSpan value)
        => Math.Max(1, (long)(value.TotalSeconds * Stopwatch.Frequency));

    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            Sources.Remove(_id);
            if (Sources.Count == 0 && _timer is not null)
            {
                _timer.Stop();
                _timer.Tick -= OnHubTick;
                _timer = null;
            }
        }
    }
}
