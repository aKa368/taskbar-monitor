using TaskbarMonitor.AgentUsage;
using Xunit;

namespace AgentUsageTests;

public sealed class ClaudeCodeUsageTests
{
    private const string FakeToken = "claude-oauth-fake-token-0123456789";

    private const string UsagePayload = """
    {
      "five_hour": { "utilization": 33.0, "resets_at": "2026-07-14T17:30:00+00:00" },
      "seven_day": { "utilization": 88.0, "resets_at": "2026-07-19T12:00:00+00:00" },
      "limits": [
        { "kind": "session", "percent": 33 },
        { "kind": "weekly_all", "percent": 88 }
      ]
    }
    """;

    [Fact]
    public void ParsesFiveHourAndSevenDayWindows()
    {
        var parsed = ClaudeCodeUsage.ParseResponse(UsagePayload);
        Assert.NotNull(parsed);

        Assert.NotNull(parsed!.FiveHour);
        Assert.Equal(33, parsed.FiveHour!.UtilizationPercent);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 17, 30, 0, TimeSpan.Zero), parsed.FiveHour.ResetsAt);

        Assert.NotNull(parsed.SevenDay);
        Assert.Equal(88, parsed.SevenDay!.UtilizationPercent);
        Assert.Equal(new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero), parsed.SevenDay.ResetsAt);
    }

    [Fact]
    public void HandlesNullBucketsGracefully()
    {
        var parsed = ClaudeCodeUsage.ParseResponse(
            "{\"five_hour\": null, \"seven_day\": {\"utilization\": 10.0}}");
        Assert.NotNull(parsed);
        Assert.Null(parsed!.FiveHour);
        Assert.Equal(10, parsed.SevenDay!.UtilizationPercent);
    }

    [Fact]
    public void MalformedPayloadReturnsNull()
    {
        Assert.Null(ClaudeCodeUsage.ParseResponse("not json"));
        Assert.Null(ClaudeCodeUsage.ParseResponse("{}"));
    }

    [Fact]
    public async Task FetchSendsBearerAndBetaHeaders()
    {
        var stub = new StubHttpHandler()
            .On("GET", "/api/oauth/usage", (200, UsagePayload));
        using var usage = new ClaudeCodeUsage(stub, credentialReader: () => "{\"access_token\":\"" + FakeToken + "\"}");

        var data = await usage.FetchAsync();
        Assert.NotNull(data);
        Assert.Null(data!.Error);
        Assert.Equal(33, data.UsedPercent5h);
        Assert.Equal(88, data.UsedPercent7d);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(FakeToken, request.Headers.Authorization?.Parameter);
        Assert.True(request.Headers.TryGetValues("anthropic-beta", out var beta));
        Assert.Equal("oauth-2025-04-20", Assert.Single(beta));
    }

    [Fact]
    public async Task RateLimitedResponseReturnsFailureWithoutThrowing()
    {
        var stub = new StubHttpHandler()
            .On("GET", "/api/oauth/usage", (429, "{\"error\":{\"type\":\"rate_limit_error\"}}"));
        using var usage = new ClaudeCodeUsage(stub, credentialReader: () => "{\"access_token\":\"" + FakeToken + "\"}");

        var data = await usage.FetchAsync();
        Assert.NotNull(data);
        Assert.Contains("429", data!.Error);
    }

    [Fact]
    public async Task MissingTokenReturnsFailureWithoutThrowing()
    {
        using var usage = new ClaudeCodeUsage(credentialReader: () => null);
        var data = await usage.FetchAsync();
        Assert.NotNull(data);
        Assert.Contains("no OAuth token", data!.Error);
    }

    [Fact]
    public async Task TokenNeverLeaksIntoErrorOnNetworkFailure()
    {
        using var usage = new ClaudeCodeUsage(new NetworkDownHandler(), credentialReader: () => "{\"access_token\":\"" + FakeToken + "\"}");
        var data = await usage.FetchAsync();
        Assert.NotNull(data);
        Assert.NotNull(data!.Error);
        AssertNoSecret.DoesNotContain(data.Error, FakeToken);
    }
}
