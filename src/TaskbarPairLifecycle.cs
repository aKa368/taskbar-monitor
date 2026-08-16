using System.Diagnostics;
using System.Runtime.CompilerServices;

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
    private static readonly TimeSpan RecoveryDebounce = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan SuccessfulSwapCooldown = TimeSpan.FromMilliseconds(250);

    private readonly Func<ITaskbarWindowPair> _factory;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _recoveryTaskSync = new();

    private ITaskbarWindowPair? _current;
    private Task? _recoveryTask;
    private int _recoveryDirty;
    private int _disposed;
    private long _generation;
    private long _pairSequence;
    private long _lastSuccessfulSwapTimestamp;

    internal TaskbarPairLifecycle(Func<ITaskbarWindowPair> factory, Func<int, TimeSpan>? backoff = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _backoff = backoff ?? (attempt => TimeSpan.FromMilliseconds(100 * (attempt + 1)));
    }

    internal long Generation => Interlocked.Read(ref _generation);

    public Task StartAsync() => SignalRecoveryAsync();

    public Task SignalRecoveryAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.CompletedTask;

        Interlocked.Exchange(ref _recoveryDirty, 1);
        lock (_recoveryTaskSync)
        {
            if (_recoveryTask is { IsCompleted: false })
                return _recoveryTask;

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _recoveryTask = completion.Task;
            _ = RunRecoveryLoopAsync(completion);
            return completion.Task;
        }
    }

    private async Task RunRecoveryLoopAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            while (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _recoveryDirty, 0) != 0)
            {
                if (_current is not null)
                    await Task.Delay(RecoveryDebounce, _shutdown.Token).ConfigureAwait(true);

                await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(true);
                try
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        break;

                    var previous = _current;
                    await DelayAfterSuccessfulSwapAsync(_shutdown.Token).ConfigureAwait(true);

                    var attached = false;
                    const int maxAttachAttempts = 3;
                    for (var attempt = 0; !attached && Volatile.Read(ref _disposed) == 0 && attempt < maxAttachAttempts; attempt++)
                    {
                        ITaskbarWindowPair? candidate = null;
                        var pairId = Interlocked.Increment(ref _pairSequence);
                        try
                        {
                            Trace.WriteLine($"[TaskbarPairLifecycle] create candidate={pairId} generation={Generation}");
                            candidate = _factory();
                            if (ReferenceEquals(previous, candidate))
                                throw new InvalidOperationException("Taskbar pair factory returned the current pair instance.");

                            candidate.TaskbarChanged += OnTaskbarChanged;
                            await candidate.AttachAccountAsync(_shutdown.Token).ConfigureAwait(true);
                            await candidate.AttachSystemAsync(_shutdown.Token).ConfigureAwait(true);
                            candidate.Show();
                            await Task.Delay(100, _shutdown.Token).ConfigureAwait(true);
                            candidate.Show();

                            _current = candidate;
                            if (previous is not null)
                                Interlocked.Exchange(ref _lastSuccessfulSwapTimestamp, Stopwatch.GetTimestamp());
                            Interlocked.Increment(ref _generation);
                            Trace.WriteLine($"[TaskbarPairLifecycle] swap candidate={pairId} generation={Generation}");

                            if (previous is not null)
                                DetachAndClose(previous, "previous");

                            attached = true;
                        }
                        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                        {
                            DetachAndClose(candidate, $"cancelled-candidate-{pairId}");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[TaskbarPairLifecycle] candidate={pairId} attempt={attempt + 1} failed: {ex}");
                            DetachAndClose(candidate, $"failed-candidate-{pairId}");
                            if (attempt + 1 < maxAttachAttempts)
                                await Task.Delay(_backoff(attempt), _shutdown.Token).ConfigureAwait(true);
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            Trace.WriteLine("[TaskbarPairLifecycle] recovery cancelled");
        }
        catch (Exception ex)
        {
            failure = ex;
            Trace.WriteLine($"[TaskbarPairLifecycle] recovery loop failed: {ex}");
        }
        finally
        {
            TaskCompletionSource? nextCompletion = null;
            lock (_recoveryTaskSync)
            {
                if (ReferenceEquals(_recoveryTask, completion.Task))
                {
                    _recoveryTask = null;
                    if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _recoveryDirty) != 0)
                    {
                        nextCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _recoveryTask = nextCompletion.Task;
                    }
                }
            }

            if (failure is not null)
                completion.TrySetException(failure);
            else
                completion.TrySetResult();

            if (nextCompletion is not null)
                _ = RunRecoveryLoopAsync(nextCompletion);
        }
    }

    private async Task DelayAfterSuccessfulSwapAsync(CancellationToken cancellationToken)
    {
        var last = Interlocked.Read(ref _lastSuccessfulSwapTimestamp);
        if (last == 0)
            return;

        var elapsed = Stopwatch.GetElapsedTime(last);
        var remaining = SuccessfulSwapCooldown - elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(true);
    }

    private async void OnTaskbarChanged(object? sender, EventArgs e)
    {
        try
        {
            await SignalRecoveryAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TaskbarPairLifecycle] TaskbarChanged callback failed: {ex}");
        }
    }

    private static void DetachAndClose(ITaskbarWindowPair? pair, string reason)
    {
        if (pair is null)
            return;

        var identity = RuntimeHelpers.GetHashCode(pair);
        try
        {
            pair.Close();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TaskbarPairLifecycle] close pair={identity} reason={reason} failed: {ex}");
        }

        try
        {
            pair.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TaskbarPairLifecycle] dispose pair={identity} reason={reason} failed: {ex}");
        }
    }

    public void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _ = CloseCurrentAfterRecoveryAsync();
    }

    private async Task CloseCurrentAfterRecoveryAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var current = _current;
                _current = null;
                DetachAndClose(current, "shutdown");
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        RequestShutdown();

        Task? recovery;
        lock (_recoveryTaskSync)
            recovery = _recoveryTask;

        if (recovery is not null)
        {
            try { await recovery.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }

        await CloseCurrentAfterRecoveryAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _gate.Dispose();
    }
}
