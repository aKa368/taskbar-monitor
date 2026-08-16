using TaskbarMonitor;
using Xunit;

namespace ConfigTests;

public sealed class TaskbarPairLifecycleTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AttachFailureRollsBackBothWindows(bool failAccount, bool failSystem)
    {
        var pair = new FakePair(failAccount, failSystem);
        await using var lifecycle = new TaskbarPairLifecycle(() => pair, _ => TimeSpan.Zero);
        await lifecycle.StartAsync();
        Assert.True(pair.Closed);
        Assert.True(pair.Disposed);
        Assert.False(pair.Shown);
    }

    [Fact]
    public async Task InitialFailureRetriesAndPublishesOnePair()
    {
        int created = 0;
        var pairs = new List<FakePair>();
        await using var lifecycle = new TaskbarPairLifecycle(() =>
        {
            var pair = new FakePair(failAccount: Interlocked.Increment(ref created) == 1, failSystem: false);
            pairs.Add(pair);
            return pair;
        }, _ => TimeSpan.Zero);
        await lifecycle.StartAsync();
        Assert.Equal(2, created);
        Assert.True(pairs[0].Closed);
        Assert.True(pairs[1].Shown);
    }

    [Fact]
    public async Task ConcurrentRecoveryEventsCreateOnlyOneReplacement()
    {
        int created = 0;
        var first = new FakePair(false, false);
        await using var lifecycle = new TaskbarPairLifecycle(() => Interlocked.Increment(ref created) == 1 ? first : new FakePair(false, false, 100), _ => TimeSpan.Zero);
        await lifecycle.StartAsync();
        for (int index = 0; index < 8; index++) first.Raise();
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task EventDuringCandidateAttachIsReplayedAsOneAdditionalGeneration()
    {
        int created = 0;
        var attachStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAttach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePair? attaching = null;
        await using var lifecycle = new TaskbarPairLifecycle(() =>
        {
            int number = Interlocked.Increment(ref created);
            var pair = number == 2
                ? new FakePair(false, false, attachStarted: attachStarted, releaseAttach: releaseAttach)
                : new FakePair(false, false);
            if (number == 2) attaching = pair;
            return pair;
        }, _ => TimeSpan.Zero);

        await lifecycle.StartAsync();
        Task recovery = lifecycle.SignalRecoveryAsync();
        await attachStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        attaching!.Raise();
        releaseAttach.SetResult();
        await recovery;

        Assert.Equal(3, created);
        Assert.Equal(3, lifecycle.Generation);
    }

    [Fact]
    public async Task CandidateIsSubscribedBeforeItsFirstAttach()
    {
        int created = 0;
        await using var lifecycle = new TaskbarPairLifecycle(() =>
        {
            var pair = new FakePair(false, false, raiseDuringAttach: Interlocked.Increment(ref created) == 1);
            return pair;
        }, _ => TimeSpan.Zero);

        await lifecycle.StartAsync();

        Assert.Equal(2, created);
        Assert.Equal(2, lifecycle.Generation);
    }

    [Fact]
    public async Task EventDuringRetryBackoffIsReplayedAfterSuccessfulReplacement()
    {
        int created = 0;
        var failedClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lifecycle = new TaskbarPairLifecycle(() =>
        {
            int number = Interlocked.Increment(ref created);
            return number == 2
                ? new FakePair(true, false, closed: failedClosed)
                : new FakePair(false, false);
        }, _ => TimeSpan.FromMilliseconds(100));

        await lifecycle.StartAsync();
        Task recovery = lifecycle.SignalRecoveryAsync();
        await failedClosed.Task.WaitAsync(TestContext.Current.CancellationToken);
        _ = lifecycle.SignalRecoveryAsync();
        await recovery;

        Assert.Equal(4, created);
        Assert.Equal(3, lifecycle.Generation);
    }

    [Fact]
    public async Task ExplorerRestartRecoversWithinBoundUnderCpuContention()
    {
        await using var lifecycle = new TaskbarPairLifecycle(
            () => new FakePair(false, false),
            _ => TimeSpan.Zero);
        await lifecycle.StartAsync();

        var loadTask = Task.Run(() =>
        {
            var until = System.Diagnostics.Stopwatch.GetTimestamp()
                + (long)(System.Diagnostics.Stopwatch.Frequency * 0.35);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
                System.Threading.Thread.SpinWait(20_000);
        });

        await Task.Delay(25, TestContext.Current.CancellationToken);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await lifecycle.SignalRecoveryAsync();
        stopwatch.Stop();
        await loadTask;

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Explorer recovery took {stopwatch.Elapsed.TotalMilliseconds:F0} ms under CPU contention.");
    }
    [Fact]
    public async Task RepeatedExplorerRecoveriesCreateOneHealthyPairPerNotification()
    {
        var created = 0;
        var pairs = new List<FakePair>();
        await using var lifecycle = new TaskbarPairLifecycle(() =>
        {
            var pair = new FakePair(false, false);
            pairs.Add(pair);
            Interlocked.Increment(ref created);
            return pair;
        }, _ => TimeSpan.Zero);

        await lifecycle.StartAsync();
        const int crashCount = 5;
        for (var i = 0; i < crashCount; i++)
            await lifecycle.SignalRecoveryAsync();

        Assert.Equal(crashCount + 1, created);
        Assert.Equal(crashCount + 1, lifecycle.Generation);
        Assert.All(pairs.Take(crashCount), pair => Assert.True(pair.Closed));
        Assert.True(pairs[^1].Shown);
        Assert.False(pairs[^1].Closed);
        Assert.False(pairs[^1].Disposed);
    }
    [Fact]
    public async Task ShutdownDuringRetryDelayPreventsRecreation()
    {
        int created = 0;
        var lifecycle = new TaskbarPairLifecycle(() => { Interlocked.Increment(ref created); return new FakePair(true, false); }, _ => TimeSpan.FromSeconds(30));
        Task start = lifecycle.StartAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await lifecycle.DisposeAsync();
        try { await start; } catch (OperationCanceledException) { }
        Assert.Equal(1, created);
    }

    private sealed class FakePair(
        bool failAccount,
        bool failSystem,
        int attachDelayMs = 0,
        bool raiseDuringAttach = false,
        TaskCompletionSource? attachStarted = null,
        TaskCompletionSource? releaseAttach = null,
        TaskCompletionSource? closed = null) : ITaskbarWindowPair
    {
        public event EventHandler? TaskbarChanged;
        public bool Shown { get; private set; }
        public bool Closed { get; private set; }
        public bool Disposed { get; private set; }
        public async Task AttachAccountAsync(CancellationToken cancellationToken)
        {
            if (failAccount) throw new InvalidOperationException();
            if (raiseDuringAttach) Raise();
            attachStarted?.SetResult();
            if (releaseAttach is not null) await releaseAttach.Task.WaitAsync(cancellationToken);
            await Delay(cancellationToken);
        }
        public Task AttachSystemAsync(CancellationToken cancellationToken) => failSystem ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask;
        public void Show() => Shown = true;
        public void Close() { Closed = true; closed?.TrySetResult(); }
        public void Dispose() => Disposed = true;
        public void Raise() => TaskbarChanged?.Invoke(this, EventArgs.Empty);
        private Task Delay(CancellationToken cancellationToken) => attachDelayMs == 0 ? Task.CompletedTask : Task.Delay(attachDelayMs, cancellationToken);
    }
}
