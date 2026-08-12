using System.Net;
using System.Text;
using TaskbarMonitor.AgentUsage;
using Xunit;

namespace AgentUsageTests;

public sealed class CodexUsageTests
{
    private const string FakeToken = "fixture-access-token-for-tests";
    private const string FakeAccount = "acct-12345";

    // Faithful GET /backend-api/wham/usage body (shape from a live Plus account).
    private const string UsagePayload = """
    {
      "plan_type": "plus",
      "rate_limit": {
        "allowed": true,
        "limit_reached": false,
        "primary_window": {
          "used_percent": 23,
          "limit_window_seconds": 18000,
          "reset_after_seconds": 12266,
          "reset_at": 1781276043
        },
        "secondary_window": {
          "used_percent": 6,
          "limit_window_seconds": 604800,
          "reset_after_seconds": 359170,
          "reset_at": 1781622947
        }
      },
      "additional_rate_limits": null,
      "credits": { "has_credits": false, "unlimited": false, "balance": "0" },
      "rate_limit_reached_type": null,
      "promo": null
    }
    """;

    private static string WriteAuthJson()
    {
        string path = Path.Combine(Path.GetTempPath(), "codex-auth-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            "{\"auth_mode\":\"chatgpt\",\"OPENAI_API_KEY\":null," +
            "\"tokens\":{\"id_token\":\"x\",\"access_token\":\"" + FakeToken + "\"," +
            "\"refresh_token\":\"r\",\"account_id\":\"" + FakeAccount + "\"}," +
            "\"last_refresh\":\"2026-08-10T00:00:00Z\"}");
        return path;
    }

    [Fact]
    public void ParsesWhamUsagePayloadWithWindowsAndCredits()
    {
        var parsed = CodexUsage.ParseResponse(UsagePayload);
        Assert.NotNull(parsed);

        Assert.NotNull(parsed!.Primary);
        Assert.Equal(23, parsed.Primary!.UsedPercent);
        Assert.Equal(300, parsed.Primary.WindowMinutes); // 18000s -> 300m
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1781276043), parsed.Primary.ResetsAt);

        Assert.NotNull(parsed.Secondary);
        Assert.Equal(6, parsed.Secondary!.UsedPercent);
        Assert.Equal(10080, parsed.Secondary.WindowMinutes); // 604800s -> 10080m
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1781622947), parsed.Secondary.ResetsAt);
    }

    [Fact]
    public void PrimaryOnlyPayloadStillProducesChatGptFiveHourUsage()
    {
        const string primaryOnly = """
        { "rate_limit": { "primary_window": { "used_percent": 37, "limit_window_seconds": 18000, "reset_at": 1781276043 } } }
        """;

        var parsed = CodexUsage.ParseResponse(primaryOnly);
        var data = CodexUsage.ToUsageData(parsed!);

        Assert.Equal(37, data.UsedPercent5h);
        Assert.Null(data.UsedPercent7d);
    }

    [Fact]
    public void ClassifiesWindowsBySizePrimaryIsFiveHourSecondaryIsSevenDay()
    {
        var parsed = CodexUsage.ParseResponse(UsagePayload);
        var data = CodexUsage.ToUsageData(parsed!, new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(23, data.UsedPercent5h);
        Assert.Equal(6, data.UsedPercent7d);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1781276043), data.ResetsAt5h);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1781622947), data.ResetsAt7d);
    }

    [Fact]
    public void MalformedJsonReturnsNull()
    {
        Assert.Null(CodexUsage.ParseResponse("{ not json"));
        Assert.Null(CodexUsage.ParseResponse("[]"));
        Assert.Null(CodexUsage.ParseResponse("{\"rate_limit\":{}}"));
    }

    [Fact]
    public async Task FetchAsyncReadsAuthFileAndSendsBearerPlusAccountHeaders()
    {
        string authPath = WriteAuthJson();
        try
        {
            var stub = new StubHttpHandler()
                .On("GET", "/backend-api/wham/usage", (200, UsagePayload));
            using var usage = new CodexUsage(stub, authPath);
            var data = await usage.FetchAsync();

            Assert.NotNull(data);
            Assert.Null(data!.Error);
            Assert.Equal(23, data.UsedPercent5h);
            Assert.Equal(6, data.UsedPercent7d);

            var request = Assert.Single(stub.Requests);
            Assert.Equal(FakeToken, request.Headers.Authorization?.Parameter);
            Assert.True(request.Headers.TryGetValues("ChatGPT-Account-Id", out var ids));
            Assert.Equal(FakeAccount, Assert.Single(ids));
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task UnauthorizedDoesNotRefreshOrRetry()
    {
        string authPath = WriteAuthJson();
        try
        {
            var stub = new StubHttpHandler()
                .On("GET", "/backend-api/wham/usage", (401, "{\"error\":\"token expired\"}"));
            using var usage = new CodexUsage(stub, authPath);
            var data = await usage.FetchAsync();

            Assert.NotNull(data);
            Assert.Contains("401", data!.Error);
            Assert.Null(data.UsedPercent5h);
            Assert.Equal(1, stub.RequestCount);
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task PersistentUnauthorizedReturnsRedactedFailure()
    {
        string authPath = WriteAuthJson();
        try
        {
            var stub = new StubHttpHandler()
                .On("GET", "/backend-api/wham/usage", (401, "{\"error\":\"bad token\"}"));
            using var usage = new CodexUsage(stub, authPath);
            var data = await usage.FetchAsync();

            Assert.NotNull(data);
            Assert.NotNull(data!.Error);
            Assert.Contains("401", data.Error);
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task MissingAuthFileReturnsFailureWithoutThrowing()
    {
        using var usage = new CodexUsage(authPath: "Z:\\missing\\auth.json");
        var data = await usage.FetchAsync();
        Assert.NotNull(data);
        Assert.Contains("auth.json", data!.Error);
    }

    [Fact]
    public async Task NetworkFailureIsCaughtAndTokenNeverLeaksIntoError()
    {
        string authPath = WriteAuthJson();
        try
        {
            using var usage = new CodexUsage(new NetworkDownHandler(), authPath);
            var data = await usage.FetchAsync();
            Assert.NotNull(data);
            Assert.NotNull(data!.Error);
            AssertNoSecret.DoesNotContain(data.Error, FakeToken);
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public async Task ExceptionMessageCarryingTheTokenIsRedacted()
    {
        string authPath = WriteAuthJson();
        try
        {
            var leaky = new StubHttpHandler();
            leaky.On("GET", "/backend-api/wham/usage", _ => throw new HttpRequestException($"auth rejected with bearer {FakeToken}"));
            using var usage = new CodexUsage(leaky, authPath);
            var data = await usage.FetchAsync();

            Assert.NotNull(data);
            Assert.NotNull(data!.Error);
            AssertNoSecret.DoesNotContain(data.Error, FakeToken);
            Assert.Contains("[REDACTED]", data.Error);
        }
        finally
        {
            TestDb.Delete(authPath);
        }
    }

    [Fact]
    public void ReadAuthIgnoresMissingToken()
    {
        string path = Path.Combine(Path.GetTempPath(), "codex-auth-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"tokens\":{\"access_token\":\"\"}}");
            Assert.Null(CodexUsage.ReadAuth(path));
        }
        finally
        {
            TestDb.Delete(path);
        }
    }
}
