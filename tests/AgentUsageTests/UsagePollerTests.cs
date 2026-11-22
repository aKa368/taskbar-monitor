using TaskbarMonitor.AgentUsage;
using Xunit;

namespace AgentUsageTests;

public sealed class UsagePollerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private const string QuotaSummaryPayload = """
    {
      "groups": [
        { "displayName": "5h", "buckets": [ { "window": "5h", "remainingFraction": 0.5, "resetTime": "2026-08-10T15:00:00Z" } ] },
        { "displayName": "7d", "buckets": [ { "window": "7d", "remainingFraction": 0.9, "resetTime": "2026-08-14T00:00:00Z" } ] }
      ]
    }
    """;

    private const string AntigravityPayload = """
    {
      "groups": [
        { "displayName": "5h", "buckets": [ { "window": "5h", "remainingFraction": 0.5, "resetTime": "2026-08-10T15:00:00Z" } ] },
        { "displayName": "7d", "buckets": [ { "window": "7d", "remainingFraction": 0.9, "resetTime": "2026-08-14T00:00:00Z" } ] }
      ]
    }
    """;

    private const string ClaudePayload = """
    {
      "five_hour": { "utilization": 33.0, "resets_at": "2026-07-14T17:30:00+00:00" },
      "seven_day": { "utilization": 88.0, "resets_at": "2026-07-19T12:00:00+00:00" }
    }
    """;

    private const string FakeAntigravityCredential =
        "{\"token\":{\"access_token\":\"ag-fake-123456\",\"refresh_token\":\"ag-r-123456\",\"expiry\":\"2030-01-01T00:00:00Z\"}}";

    [Fact]
    public async Task RefreshOncePopulatesEveryEnabledAgent()
    {
        string ccDb = TestDb.CreateUsageHistoryDb("commandcode",
            [(TestDb.IsoUtc(DateTimeOffset.UtcNow.AddHours(-1)), 50, 10, 0.25, "ok")]);
        string ocDb = TestDb.CreateSessionDb([(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(), 0.10, 100, 20, 5, 10, 2)]);
        try
        {
            using var commandCode = new CommandCodeUsage(ccDb);
            using var openCode = new OpenCodeUsage(ocDb);
            using var antigravity = new AntigravityUsage(
                new StubHttpHandler().On("POST", "/v1internal:retrieveUserQuotaSummary", (200, AntigravityPayload)),
                credentialReader: () => FakeAntigravityCredential);
            using var claude = new ClaudeCodeUsage(
                new StubHttpHandler().On("GET", "/api/oauth/usage", (200, ClaudePayload)),
                credentialReader: () => "{\"access_token\":\"claude-fake-123456\"}");

            using var poller = new UsagePoller(
                new UsagePollerOptions { AntigravityEnabled = true, ClaudeEnabled = true },
                commandCode, openCode, antigravity, claude);

            await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);

            var cc = poller.Get(AgentIds.CommandCode);
            Assert.NotNull(cc);
            Assert.Equal(0.25, cc!.Last5h!.Cost!.Value, 6);

            var oc = poller.Get(AgentIds.OpenCode);
            Assert.NotNull(oc);
            Assert.Equal(100, oc!.Last5h!.TokensInput);

            var ag = poller.Get(AgentIds.Antigravity);
            Assert.NotNull(ag);
            Assert.Equal(50, ag!.UsedPercent5h!.Value, 3);

            var cl = poller.Get(AgentIds.Claude);
            Assert.NotNull(cl);
            Assert.Equal(33, cl!.UsedPercent5h);

            Assert.Equal(4, poller.GetAll().Count);
        }
        finally
        {
            TestDb.Delete(ccDb);
            TestDb.Delete(ocDb);
        }
    }

    [Fact]
    public async Task FailedPollKeepsLastKnownGoodValue()
    {
        int calls = 0;
        var flaky = new StubHttpHandler();
        flaky.On("POST", "/v1internal:retrieveUserQuotaSummary", _ =>
            Interlocked.Increment(ref calls) == 1
                ? (200, AntigravityPayload)
                : (500, "{\"error\":\"boom\"}"));
        using var antigravity = new AntigravityUsage(flaky, credentialReader: () => FakeAntigravityCredential);
        using var poller = new UsagePoller(
            new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = true, ClaudeEnabled = false },
            antigravity: antigravity);

        Assert.Null(poller.Get(AgentIds.Antigravity));

        await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);
        var good = poller.Get(AgentIds.Antigravity);
        Assert.NotNull(good);
        Assert.Null(good!.Error);
        Assert.Equal(50, good.UsedPercent5h!.Value, 3);

        await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);
        var afterFailure = poller.Get(AgentIds.Antigravity);
        Assert.NotNull(afterFailure);
        Assert.NotNull(afterFailure!.Error);
        Assert.Equal(50, afterFailure.UsedPercent5h!.Value); // last known good preserved
    }

    [Fact]
    public async Task MissingSqliteDatabaseDoesNotCrashPoller()
    {
        using var commandCode = new CommandCodeUsage("Z:\\missing\\db.sqlite");
        using var openCode = new OpenCodeUsage("Z:\\missing\\opencode.db");
        using var antigravity = new AntigravityUsage(new NetworkDownHandler(), credentialReader: () => FakeAntigravityCredential);

        using var poller = new UsagePoller(null, commandCode, openCode, antigravity);
        await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);

        Assert.Null(poller.Get(AgentIds.CommandCode));
        Assert.Null(poller.Get(AgentIds.OpenCode));
        Assert.NotNull(poller.Get(AgentIds.Antigravity));
    }

    [Fact]
    public async Task DisabledAgentsAreNotPolled()
    {
        string ccDb = TestDb.CreateUsageHistoryDb("commandcode",
            [(TestDb.IsoUtc(Now.AddHours(-1)), 50, 10, 0.25, "ok")]);
        try
        {
            var antigravityStub = new StubHttpHandler().On("POST", "/v1internal:retrieveUserQuotaSummary", (200, AntigravityPayload));
            using var antigravity = new AntigravityUsage(antigravityStub, credentialReader: () => FakeAntigravityCredential);
            using var commandCode = new CommandCodeUsage(ccDb);
            using var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
                commandCode, antigravity: antigravity);

            await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);
            Assert.Null(poller.Get(AgentIds.CommandCode));
            Assert.Null(poller.Get(AgentIds.Antigravity));
            Assert.Equal(0, antigravityStub.RequestCount);
        }
        finally
        {
            TestDb.Delete(ccDb);
        }
    }

    [Fact]
    public async Task ReconfigureEnablesAntigravityWithoutRecreatingPoller()
    {
        var handler = new StubHttpHandler().On("POST", "/v1internal:retrieveUserQuotaSummary", (200, AntigravityPayload));
        using var antigravity = new AntigravityUsage(handler, credentialReader: () => FakeAntigravityCredential);
        using var poller = new UsagePoller(
            new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
            antigravity: antigravity);

        await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Null(poller.Get(AgentIds.Antigravity));

        poller.Reconfigure(new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = true, ClaudeEnabled = false });
        await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(50, poller.Get(AgentIds.Antigravity)?.UsedPercent5h);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ReconfigureStartsNewlyEnabledGroupAfterStartingWithEverythingDisabled()
    {
        var handler = new StubHttpHandler().On("POST", "/v1internal:retrieveUserQuotaSummary", (200, AntigravityPayload));
        using var antigravity = new AntigravityUsage(handler, credentialReader: () => FakeAntigravityCredential);
        await using var poller = new UsagePoller(new UsagePollerOptions
        {
            CommandCodeEnabled = false,
            OpenCodeEnabled = false,
            AntigravityEnabled = false,
            ClaudeEnabled = false,
            ApiPollInterval = TimeSpan.FromHours(1)
        }, antigravity: antigravity);

        poller.Start();
        poller.Reconfigure(new UsagePollerOptions
        {
            CommandCodeEnabled = false,
            OpenCodeEnabled = false,
            AntigravityEnabled = true,
            ClaudeEnabled = false,
            ApiPollInterval = TimeSpan.FromHours(1)
        });

        await WaitUntilAsync(() => handler.RequestCount == 1);
        Assert.Equal(50, poller.Get(AgentIds.Antigravity)?.UsedPercent5h);
    }

    [Fact]
    public async Task StartExplicitlyRefreshesApiBeforeLongTimerInterval()
    {
        var handler = new StubHttpHandler().On("POST", "/v1internal:retrieveUserQuotaSummary", (200, AntigravityPayload));
        using var antigravity = new AntigravityUsage(handler, credentialReader: () => FakeAntigravityCredential);
        await using var poller = new UsagePoller(new UsagePollerOptions
        {
            CommandCodeEnabled = false,
            OpenCodeEnabled = false,
            AntigravityEnabled = true,
            ClaudeEnabled = false,
            ApiPollInterval = TimeSpan.FromHours(1)
        }, antigravity: antigravity);

        poller.Start();

        await WaitUntilAsync(() => poller.Get(AgentIds.Antigravity) is not null);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(50, poller.Get(AgentIds.Antigravity)?.UsedPercent5h);
    }

    [Fact]
    public async Task StartAsyncCompletionRepresentsInitialApiRefresh()
    {
        var slow = new SlowHttpHandler(TimeSpan.FromMilliseconds(250), (200, AntigravityPayload), honorCancellation: true);
        using var antigravity = new AntigravityUsage(slow, credentialReader: () => FakeAntigravityCredential);
        await using var poller = new UsagePoller(new UsagePollerOptions
        {
            CommandCodeEnabled = false,
            OpenCodeEnabled = false,
            AntigravityEnabled = true,
            ClaudeEnabled = false,
            ApiPollInterval = TimeSpan.FromHours(1)
        }, antigravity: antigravity);

        Task initial = poller.StartAsync();
        await slow.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(initial.IsCompleted);
        await initial.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(50, poller.Get(AgentIds.Antigravity)?.UsedPercent5h);
    }

    [Fact]
    public async Task BlockedSqliteInitialRefreshDoesNotDelayApiInitialRefresh()
    {
        string db = TestDb.CreateUsageHistoryDb("commandcode",
            [(TestDb.IsoUtc(DateTimeOffset.UtcNow.AddMinutes(-1)), 10, 5, 0.10, "ok")]);
        try
        {
            using var fileLock = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var handler = new StubHttpHandler().On("POST", "/v1internal:retrieveUserQuotaSummary", (200, AntigravityPayload));
            using var commandCode = new CommandCodeUsage(db);
            using var antigravity = new AntigravityUsage(handler, credentialReader: () => FakeAntigravityCredential);
            await using var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = true,
                OpenCodeEnabled = false,
                AntigravityEnabled = true,
                ClaudeEnabled = false,
                SqlitePollInterval = TimeSpan.FromHours(1),
                ApiPollInterval = TimeSpan.FromHours(1)
            }, commandCode: commandCode, antigravity: antigravity);

            poller.Start();
            await WaitUntilAsync(() => poller.Get(AgentIds.Antigravity) is not null);

            Assert.Equal(1, handler.RequestCount);
            Assert.Null(poller.Get(AgentIds.CommandCode));
        }
        finally
        {
            TestDb.Delete(db);
        }
    }

    [Fact]
    public async Task StartupPollsSqliteWhileApiRequestIsInFlight()
    {
        string db = TestDb.CreateUsageHistoryDb("commandcode",
            [(TestDb.IsoUtc(DateTimeOffset.UtcNow.AddMinutes(-1)), 10, 5, 0.10, "ok")]);
        var slowApi = new SlowHttpHandler(TimeSpan.FromMilliseconds(600), (200, AntigravityPayload), honorCancellation: true);
        try
        {
            using var commandCode = new CommandCodeUsage(db);
            using var antigravity = new AntigravityUsage(slowApi, credentialReader: () => FakeAntigravityCredential);
            await using var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = true,
                OpenCodeEnabled = false,
                AntigravityEnabled = true,
                ClaudeEnabled = false,
                SqlitePollInterval = TimeSpan.FromHours(1),
                ApiPollInterval = TimeSpan.FromHours(1)
            }, commandCode: commandCode, antigravity: antigravity);

            poller.Start();
            await slowApi.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => poller.Get(AgentIds.CommandCode) is not null);

            Assert.Equal(0, slowApi.CompletedRequests);
            Assert.NotNull(poller.Get(AgentIds.CommandCode));
        }
        finally
        {
            TestDb.Delete(db);
        }
    }

    [Fact]
    public async Task HotEnableQueuesOneImmediateApiRefreshBehindInFlightPoll()
    {
        var slowOld = new SlowHttpHandler(TimeSpan.FromMilliseconds(350), (200, QuotaSummaryPayload), honorCancellation: true);
        var claudeHandler = new StubHttpHandler().On("GET", "/api/oauth/usage", (200, ClaudePayload));
        try
        {
            using var antigravity = new AntigravityUsage(slowOld, credentialReader: () => FakeAntigravityCredential);
            using var claude = new ClaudeCodeUsage(claudeHandler, credentialReader: () => "{\"access_token\":\"claude-fake-123456\"}");
            await using var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = false,
                OpenCodeEnabled = false,
                AntigravityEnabled = true,
                ClaudeEnabled = false,
                ApiPollInterval = TimeSpan.FromHours(1)
            }, antigravity: antigravity, claude: claude);
            poller.Start();
            await slowOld.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            poller.Reconfigure(new UsagePollerOptions
            {
                CommandCodeEnabled = false,
                OpenCodeEnabled = false,
                AntigravityEnabled = true,
                ClaudeEnabled = true,
                ApiPollInterval = TimeSpan.FromHours(1)
            });

            await WaitUntilAsync(() => poller.Get(AgentIds.Claude) is not null);
            Assert.Equal(1, claudeHandler.RequestCount);
        }
        finally { }
    }

    [Fact]
    public async Task DisposeDropsPendingHotEnableRefresh()
    {
        var hangingOld = new SlowHttpHandler(TimeSpan.FromSeconds(30), (200, QuotaSummaryPayload), honorCancellation: true);
        var claudeHandler = new StubHttpHandler().On("GET", "/api/oauth/usage", (200, ClaudePayload));
        try
        {
            using var antigravity = new AntigravityUsage(hangingOld, credentialReader: () => FakeAntigravityCredential);
            using var claude = new ClaudeCodeUsage(claudeHandler, credentialReader: () => "{\"access_token\":\"claude-fake-123456\"}");
            var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = false,
                OpenCodeEnabled = false,
                AntigravityEnabled = true,
                ClaudeEnabled = false,
                ApiPollInterval = TimeSpan.FromHours(1)
            }, antigravity: antigravity, claude: claude);
            poller.Start();
            await hangingOld.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            poller.Reconfigure(new UsagePollerOptions
            {
                CommandCodeEnabled = false,
                OpenCodeEnabled = false,
                AntigravityEnabled = true,
                ClaudeEnabled = true,
                ApiPollInterval = TimeSpan.FromHours(1)
            });
            await Task.Delay(50, TestContext.Current.CancellationToken);
            await poller.DisposeAsync();
            Assert.Equal(0, claudeHandler.RequestCount);
        }
        finally { }
    }

    [Fact]
    public async Task ConcurrentRefreshesAreSerializedAndSafe()
    {
        var handler = new StubHttpHandler().On("POST", "/v1internal:retrieveUserQuotaSummary", (200, AntigravityPayload));
        using var antigravity = new AntigravityUsage(handler, credentialReader: () => FakeAntigravityCredential);
        using var poller = new UsagePoller(
            new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = true, ClaudeEnabled = false },
            antigravity: antigravity);

        var tasks = Enumerable.Range(0, 8).Select(_ => poller.RefreshOnceAsync(TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        var data = poller.Get(AgentIds.Antigravity);
        Assert.NotNull(data);
        Assert.Equal(50, data!.UsedPercent5h!.Value, 3);
    }

    [Fact]
    public async Task DisposeAsyncWaitsForInFlightRequests()
    {
        // Handler ignores cancellation: DisposeAsync must wait out the delay
        // instead of tearing down the HttpClient under the in-flight request.
        var slowHandler = new SlowHttpHandler(TimeSpan.FromMilliseconds(400), (200, AntigravityPayload), honorCancellation: false);
        try
        {
            using var antigravity = new AntigravityUsage(slowHandler, credentialReader: () => FakeAntigravityCredential);
            var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = true, ClaudeEnabled = false },
                antigravity: antigravity);

            // Start the timer-driven poll, wait until the request is actually
            // in flight, then dispose.
            poller.Start();
            await slowHandler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await poller.DisposeAsync();

            // DisposeAsync must have waited for the in-flight request to finish;
            // the handler was still resolving when the poller began shutting down.
            Assert.True(slowHandler.CompletedRequests >= 1,
                "DisposeAsync should wait for the in-flight poll before disposing HTTP clients.");
        }
        finally { }
    }

    [Fact]
    public async Task DisposeAsyncCancelsPendingWorkAndDoesNotThrow()
    {
        // Handler honors cancellation: DisposeAsync cancels and returns quickly,
        // never blocking forever on a stuck provider.
        var hangingHandler = new SlowHttpHandler(TimeSpan.FromSeconds(30), (200, AntigravityPayload), honorCancellation: true);
        try
        {
            using var antigravity = new AntigravityUsage(hangingHandler, credentialReader: () => FakeAntigravityCredential);
            var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = true, ClaudeEnabled = false },
                antigravity: antigravity);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            poller.Start();
            await hangingHandler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await poller.DisposeAsync(); // must not block forever or throw
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"DisposeAsync blocked for {stopwatch.Elapsed.TotalSeconds:F1}s on a cancelable request.");
        }
        finally { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}