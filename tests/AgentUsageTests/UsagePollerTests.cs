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

            await poller.RefreshOnceAsync();

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

            await poller.RefreshOnceAsync();
            var good = poller.Get(AgentIds.Codex);
            Assert.NotNull(good);
            Assert.Null(good!.Error);
            Assert.Equal(12, good.UsedPercent5h);

            await poller.RefreshOnceAsync();
            var afterFailure = poller.Get(AgentIds.Codex);
            Assert.NotNull(afterFailure);
            Assert.Null(afterFailure!.Error);
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
        await poller.RefreshOnceAsync();

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

            await poller.RefreshOnceAsync();
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
    public async Task ConcurrentRefreshesAreSerializedAndSafe()
    {
        string authPath = WriteCodexAuth();
        try
        {
            using var codex = new CodexUsage(new StubHttpHandler().On("GET", "/backend-api/wham/usage", (200, CodexPayload)), authPath);
            using var poller = new UsagePoller(
                new UsagePollerOptions { CommandCodeEnabled = false, OpenCodeEnabled = false, AntigravityEnabled = false, ClaudeEnabled = false },
                codex: codex);

            var tasks = Enumerable.Range(0, 8).Select(_ => poller.RefreshOnceAsync());
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

    private static string WriteCodexAuth()
    {
        string path = Path.Combine(Path.GetTempPath(), "codex-auth-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            "{\"tokens\":{\"access_token\":\"poller-fake-token-123456\",\"account_id\":\"acct-1\"}}");
        return path;
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
