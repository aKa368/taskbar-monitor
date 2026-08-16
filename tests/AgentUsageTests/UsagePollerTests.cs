using TaskbarMonitor.AgentUsage;
using Xunit;

namespace AgentUsageTests;

public sealed class UsagePollerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private const string CodexPayload = """
    {
      "rate_limit": {
        "primary_window": { "used_percent": 12, "limit_window_seconds": 18000, "reset_at": 1781276043 },
        "secondary_window": { "used_percent": 4, "limit_window_seconds": 604800, "reset_at": 1781622947 }
      },
      "credits": { "has_credits": false }
    }
    """;

    [Fact]
    public async Task RefreshOncePopulatesEveryEnabledAgent()
    {
        string ccDb = TestDb.CreateUsageHistoryDb("commandcode",
            [(TestDb.IsoUtc(DateTimeOffset.UtcNow.AddHours(-1)), 50, 10, 0.25, "ok")]);
        string ocDb = TestDb.CreateSessionDb([(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(), 0.10, 100, 20, 5, 10, 2)]);
        string authPath = WriteCodexAuth();
        try
        {
            using var commandCode = new CommandCodeUsage(ccDb);
            using var openCode = new OpenCodeUsage(ocDb);
            using var codex = new CodexUsage(new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload)), authPath);
            using var antigravity = new AntigravityUsage(
                new StubHttpHandler().On("POST", "/v1internal:retrieveUserQuotaSummary", (200, QuotaSummaryPayload)),
                credentialReader: () => FakeAntigravityCredential);
            using var claude = new ClaudeCodeUsage(
                new StubHttpHandler().On("GET", "/api/oauth/usage", (200, ClaudePayload)),
                credentialReader: () => "{\"access_token\":\"claude-fake-123456\"}");

            using var poller = new UsagePoller(
                new UsagePollerOptions { ClaudeEnabled = true },
                commandCode, openCode, codex, antigravity, claude);

            await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);

            var cc = poller.Get(AgentIds.CommandCode);
            Assert.NotNull(cc);
            Assert.Equal(0.25, cc!.Last5h!.Cost!.Value, 6);

            var oc = poller.Get(AgentIds.OpenCode);
            Assert.NotNull(oc);
            Assert.Equal(100, oc!.Last5h!.TokensInput);

            var cx = poller.Get(AgentIds.Codex);
            Assert.NotNull(cx);
            Assert.Equal(12, cx!.UsedPercent5h);
            Assert.Equal(4, cx.UsedPercent7d);

            var ag = poller.Get(AgentIds.Antigravity);
            Assert.NotNull(ag);
            Assert.Equal(50, ag!.UsedPercent5h!.Value, 3);

            var cl = poller.Get(AgentIds.Claude);
            Assert.NotNull(cl);
            Assert.Equal(33, cl!.UsedPercent5h);

            Assert.Equal(5, poller.GetAll().Count);
        }
        finally
        {
            TestDb.Delete(ccDb);
            TestDb.Delete(ocDb);
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task FailedPollKeepsLastKnownGoodValue()
    {
        string authPath = WriteCodexAuth();
        try
        {
            int calls = 0;
            var flaky = new StubHttpHandler();
            flaky.On("GET", "/backend-api/wham/usage", _ =>
                Interlocked.Increment(ref calls) == 1
                    ? (200, CodexPayload)
                    : (500, "{\"error\":\"boom\"}"));
            using var codex = new CodexUsage(flaky, authPath);
            using var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
                codex: codex);

            Assert.Null(poller.Get(AgentIds.Codex));

            await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);
            var good = poller.Get(AgentIds.Codex);
            Assert.NotNull(good);
            Assert.Null(good!.Error);
            Assert.Equal(12, good.UsedPercent5h);

            await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);
            var afterFailure = poller.Get(AgentIds.Codex);
            Assert.NotNull(afterFailure);
            Assert.NotNull(afterFailure!.Error);
            Assert.Equal(12, afterFailure.UsedPercent5h); // last known good preserved
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task MissingSqliteDatabaseDoesNotCrashPoller()
    {
        using var commandCode = new CommandCodeUsage("Z:\\missing\\db.sqlite");
        using var openCode = new OpenCodeUsage("Z:\\missing\\opencode.db");
        using var codex = new CodexUsage(new NetworkDownHandler());
        using var antigravity = new AntigravityUsage(new NetworkDownHandler(), credentialReader: () => FakeAntigravityCredential);

        using var poller = new UsagePoller(null, commandCode, openCode, codex, antigravity);
        await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);

        Assert.Null(poller.Get(AgentIds.CommandCode));
        Assert.Null(poller.Get(AgentIds.OpenCode));
        Assert.NotNull(poller.Get(AgentIds.Codex));
        Assert.NotNull(poller.Get(AgentIds.Antigravity));
    }

    [Fact]
    public async Task DisabledAgentsAreNotPolled()
    {
        string ccDb = TestDb.CreateUsageHistoryDb("commandcode",
            [(TestDb.IsoUtc(Now.AddHours(-1)), 50, 10, 0.25, "ok")]);
        try
        {
            var codexStub = new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload));
            using var codex = new CodexUsage(codexStub, WriteCodexAuth());
            using var commandCode = new CommandCodeUsage(ccDb);
            using var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
                commandCode, codex: codex);

            await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);
            Assert.Null(poller.Get(AgentIds.CommandCode));
            Assert.Null(poller.Get(AgentIds.Codex));
            Assert.Equal(0, codexStub.RequestCount);
        }
        finally
        {
            TestDb.Delete(ccDb);
        }
    }

    [Fact]
    public async Task ReconfigureEnablesCodexWithoutRecreatingPoller()
    {
        string authPath = WriteCodexAuth();
        try
        {
            var handler = new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload));
            using var codex = new CodexUsage(handler, authPath);
            using var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
                codex: codex);

            await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);
            Assert.Null(poller.Get(AgentIds.Codex));

            poller.Reconfigure(new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = true, AntigravityEnabled = false, ClaudeEnabled = false });
            await poller.RefreshOnceAsync(TestContext.Current.CancellationToken);

            Assert.Equal(12, poller.Get(AgentIds.Codex)?.UsedPercent5h);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task ReconfigureStartsNewlyEnabledGroupAfterStartingWithEverythingDisabled()
    {
        string authPath = WriteCodexAuth();
        try
        {
            var handler = new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload));
            using var codex = new CodexUsage(handler, authPath);
            await using var poller = new UsagePoller(
                new UsagePollerOptions
                {
                    CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = false,
                    AntigravityEnabled = false, ClaudeEnabled = false,
                    ApiPollInterval = TimeSpan.FromHours(1)
                }, codex: codex);

            poller.Start();
            poller.Reconfigure(new UsagePollerOptions
            {
                CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = true,
                AntigravityEnabled = false, ClaudeEnabled = false,
                ApiPollInterval = TimeSpan.FromHours(1)
            });

            await WaitUntilAsync(() => handler.RequestCount == 1);
            Assert.Equal(12, poller.Get(AgentIds.Codex)?.UsedPercent5h);
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task StartExplicitlyRefreshesApiBeforeLongTimerInterval()
    {
        string authPath = WriteCodexAuth();
        try
        {
            var handler = new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload));
            using var codex = new CodexUsage(handler, authPath);
            await using var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = true,
                AntigravityEnabled = false, ClaudeEnabled = false,
                ApiPollInterval = TimeSpan.FromHours(1)
            }, codex: codex);

            poller.Start();

            await WaitUntilAsync(() => poller.Get(AgentIds.Codex) is not null);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(12, poller.Get(AgentIds.Codex)?.UsedPercent5h);
        }
        finally { TestDb.Delete(authPath); }
    }

    [Fact]
    public async Task StartAsyncCompletionRepresentsInitialApiRefresh()
    {
        string authPath = WriteCodexAuth();
        var slow = new SlowHttpHandler(TimeSpan.FromMilliseconds(250), (200, CodexPayload), honorCancellation: true);
        try
        {
            using var codex = new CodexUsage(slow, authPath);
            await using var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = true,
                AntigravityEnabled = false, ClaudeEnabled = false, ApiPollInterval = TimeSpan.FromHours(1)
            }, codex: codex);

            Task initial = poller.StartAsync();
            await slow.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(initial.IsCompleted);
            await initial.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(12, poller.Get(AgentIds.Codex)?.UsedPercent5h);
        }
        finally { TestDb.Delete(authPath); }
    }

    [Fact]
    public async Task BlockedSqliteInitialRefreshDoesNotDelayApiInitialRefresh()
    {
        string db = TestDb.CreateUsageHistoryDb("commandcode",
            [(TestDb.IsoUtc(DateTimeOffset.UtcNow.AddMinutes(-1)), 10, 5, 0.10, "ok")]);
        string authPath = WriteCodexAuth();
        try
        {
            using var fileLock = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var handler = new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload));
            using var commandCode = new CommandCodeUsage(db);
            using var codex = new CodexUsage(handler, authPath);
            await using var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = true, OpenCodeEnabled = false, CodexEnabled = true,
                AntigravityEnabled = false, ClaudeEnabled = false,
                SqlitePollInterval = TimeSpan.FromHours(1), ApiPollInterval = TimeSpan.FromHours(1)
            }, commandCode: commandCode, codex: codex);

            poller.Start();
            await WaitUntilAsync(() => poller.Get(AgentIds.Codex) is not null);

            Assert.Equal(1, handler.RequestCount);
            Assert.Null(poller.Get(AgentIds.CommandCode));
        }
        finally
        {
            TestDb.Delete(db);
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task StartupPollsSqliteWhileApiRequestIsInFlight()
    {
        string db = TestDb.CreateUsageHistoryDb("commandcode",
            [(TestDb.IsoUtc(DateTimeOffset.UtcNow.AddMinutes(-1)), 10, 5, 0.10, "ok")]);
        string authPath = WriteCodexAuth();
        var slowApi = new SlowHttpHandler(TimeSpan.FromMilliseconds(600), (200, CodexPayload), honorCancellation: true);
        try
        {
            using var commandCode = new CommandCodeUsage(db);
            using var codex = new CodexUsage(slowApi, authPath);
            await using var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = true, OpenCodeEnabled = false, CodexEnabled = true,
                AntigravityEnabled = false, ClaudeEnabled = false,
                SqlitePollInterval = TimeSpan.FromHours(1), ApiPollInterval = TimeSpan.FromHours(1)
            }, commandCode: commandCode, codex: codex);

            poller.Start();
            await slowApi.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => poller.Get(AgentIds.CommandCode) is not null);

            Assert.Equal(0, slowApi.CompletedRequests);
            Assert.NotNull(poller.Get(AgentIds.CommandCode));
        }
        finally
        {
            TestDb.Delete(db);
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task HotEnableQueuesOneImmediateApiRefreshBehindInFlightPoll()
    {
        string authPath = WriteCodexAuth();
        var slowOld = new SlowHttpHandler(TimeSpan.FromMilliseconds(350), (200, QuotaSummaryPayload), honorCancellation: true);
        var codexHandler = new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload));
        try
        {
            using var antigravity = new AntigravityUsage(slowOld, credentialReader: () => FakeAntigravityCredential);
            using var codex = new CodexUsage(codexHandler, authPath);
            await using var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = false,
                AntigravityEnabled = true, ClaudeEnabled = false, ApiPollInterval = TimeSpan.FromHours(1)
            }, codex: codex, antigravity: antigravity);
            poller.Start();
            await slowOld.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            poller.Reconfigure(new UsagePollerOptions
            {
                CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = true,
                AntigravityEnabled = true, ClaudeEnabled = false, ApiPollInterval = TimeSpan.FromHours(1)
            });

            await WaitUntilAsync(() => poller.Get(AgentIds.Codex) is not null);
            Assert.Equal(1, codexHandler.RequestCount);
        }
        finally { TestDb.Delete(authPath); }
    }

    [Fact]
    public async Task DisposeDropsPendingHotEnableRefresh()
    {
        string authPath = WriteCodexAuth();
        var hangingOld = new SlowHttpHandler(TimeSpan.FromSeconds(30), (200, QuotaSummaryPayload), honorCancellation: true);
        var codexHandler = new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload));
        try
        {
            using var antigravity = new AntigravityUsage(hangingOld, credentialReader: () => FakeAntigravityCredential);
            using var codex = new CodexUsage(codexHandler, authPath);
            var poller = new UsagePoller(new UsagePollerOptions
            {
                CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = false,
                AntigravityEnabled = true, ClaudeEnabled = false, ApiPollInterval = TimeSpan.FromHours(1)
            }, codex: codex, antigravity: antigravity);
            poller.Start();
            await hangingOld.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            poller.Reconfigure(new UsagePollerOptions
            {
                CommandCodeEnabled = false, OpenCodeEnabled = false, CodexEnabled = true,
                AntigravityEnabled = true, ClaudeEnabled = false, ApiPollInterval = TimeSpan.FromHours(1)
            });
            await Task.Delay(50, TestContext.Current.CancellationToken);
            await poller.DisposeAsync();
            Assert.Equal(0, codexHandler.RequestCount);
        }
        finally { TestDb.Delete(authPath); }
    }

    [Fact]
    public async Task ConcurrentRefreshesAreSerializedAndSafe()
    {
        string authPath = WriteCodexAuth();
        try
        {
            using var codex = new CodexUsage(new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload)), authPath);
            using var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
                codex: codex);

            var tasks = Enumerable.Range(0, 8).Select(_ => poller.RefreshOnceAsync(TestContext.Current.CancellationToken));
            await Task.WhenAll(tasks);

            var data = poller.Get(AgentIds.Codex);
            Assert.NotNull(data);
            Assert.Equal(12, data!.UsedPercent5h);
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task DisposeAsyncWaitsForInFlightRequests()
    {
        string authPath = WriteCodexAuth();
        // Handler ignores cancellation: DisposeAsync must wait out the delay
        // instead of tearing down the HttpClient under the in-flight request.
        var slowHandler = new SlowHttpHandler(TimeSpan.FromMilliseconds(400), (200, CodexPayload), honorCancellation: false);
        try
        {
            using var codex = new CodexUsage(slowHandler, authPath);
            var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
                codex: codex);

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
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task DisposeAsyncCancelsPendingWorkAndDoesNotThrow()
    {
        string authPath = WriteCodexAuth();
        // Handler honors cancellation: DisposeAsync cancels and returns quickly,
        // never blocking forever on a stuck provider.
        var hangingHandler = new SlowHttpHandler(TimeSpan.FromSeconds(30), (200, CodexPayload), honorCancellation: true);
        try
        {
            using var codex = new CodexUsage(hangingHandler, authPath);
            var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
                codex: codex);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            poller.Start();
            await hangingHandler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await poller.DisposeAsync(); // must not block forever or throw
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"DisposeAsync blocked for {stopwatch.Elapsed.TotalSeconds:F1}s on a cancelable request.");
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    private static string WriteCodexAuth()
    {
        string path = Path.Combine(Path.GetTempPath(), "codex-auth-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            "{\"tokens\":{\"access_token\":\"poller-fake-token-123456\",\"account_id\":\"acct-1\"}}");
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private const string QuotaSummaryPayload = """
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
}
