namespace TaskbarMonitor;

internal interface ITaskbarWindowPair : IDisposable
{
    event EventHandler? TaskbarChanged;
    Task AttachAccountAsync(CancellationToken cancellationToken);
    Task AttachSystemAsync(CancellationToken cancellationToken);
    void Show();
    void Close();
}

internal sealed class TaskbarPairLifecycle : IAsyncDisposable
{
    private readonly Func<ITaskbarWindowPair> _factory;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITaskbarWindowPair? _current;
    private int _recovering;
    private int _recoveryDirty;
    private long _generation;
    private bool _disposed;

    internal TaskbarPairLifecycle(Func<ITaskbarWindowPair> factory, Func<int, TimeSpan>? backoff = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _backoff = backoff ?? (attempt => TimeSpan.FromMilliseconds(Math.Min(2000, 250 * (1 << Math.Min(attempt, 3)))));
    }

    internal long Generation => Interlocked.Read(ref _generation);

    public Task StartAsync()
    {
        Interlocked.Exchange(ref _recoveryDirty, 1);
        return RunRecoveryLoopAsync();
    }

    public Task SignalRecoveryAsync()
    {
        if (_disposed) return Task.CompletedTask;
        Interlocked.Exchange(ref _recoveryDirty, 1);
        return RunRecoveryLoopAsync();
    }

    private async Task RunRecoveryLoopAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
            return;

        try
        {
            // Explorer can broadcast several taskbar notifications for one restart.
            // Debounce only replacement requests; an initial startup should remain immediate.
            if (_current is not null)
                await Task.Delay(25, _shutdown.Token).ConfigureAwait(true);

            await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(true);
            try
            {
                while (!_disposed && Interlocked.Exchange(ref _recoveryDirty, 0) != 0)
                {
                    DetachAndClose(_current);
                    _current = null;

                    var attached = false;
                    const int maxAttachAttempts = 3;
                    for (var attempt = 0; !attached && !_disposed && attempt < maxAttachAttempts; attempt++)
                    {
                        _shutdown.Token.ThrowIfCancellationRequested();
                        var candidate = _factory();
                        candidate.TaskbarChanged += OnTaskbarChanged;
                        try
                        {
                            await candidate.AttachAccountAsync(_shutdown.Token).ConfigureAwait(true);
                            await candidate.AttachSystemAsync(_shutdown.Token).ConfigureAwait(true);
                            candidate.Show();
                            await Task.Delay(100, _shutdown.Token).ConfigureAwait(true);
                            candidate.Show();
                            _current = candidate;
                            Interlocked.Increment(ref _generation);
                            attached = true;
                        }
                        catch
                        {
                            DetachAndClose(candidate);
                            if (_disposed) throw;
                            if (attempt + 1 < maxAttachAttempts)
                                await Task.Delay(_backoff(attempt), _shutdown.Token).ConfigureAwait(true);
                        }
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
            if (!_disposed && Volatile.Read(ref _recoveryDirty) != 0)
                _ = RunRecoveryLoopAsync();
        }
    }

    private async void OnTaskbarChanged(object? sender, EventArgs e)
    {
        try { await SignalRecoveryAsync().ConfigureAwait(true); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private static void DetachAndClose(ITaskbarWindowPair? pair)
    {
        if (pair is null) return;
        try { pair.Close(); } catch { }
        try { pair.Dispose(); } catch { }
    }

    public void RequestShutdown()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        if (_gate.Wait(0))
        {
            try { DetachAndClose(_current); _current = null; }
            finally { _gate.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        RequestShutdown();
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _shutdown.Dispose();
        _gate.Dispose();
    }
}
