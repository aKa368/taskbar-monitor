using TaskbarMonitor.AgentUsage;
using Xunit;

namespace AgentUsageTests;

public sealed class AntigravityUsageTests
{
    private const string FakeAccess = "antigravity-fake-access-token-0123456789";
    private const string FakeRefresh = "antigravity-fake-refresh-token-0123456789";

    private const string AvailableModelsPayload = """
    {
      "models": {
        "claude-sonnet-4-5": {
          "displayName": "Claude 4 Sonnet",
          "model": "claude-sonnet-4-5",
          "label": "Claude 4 Sonnet",
          "quotaInfo": { "remainingFraction": 0.85, "resetTime": "2026-01-26T12:00:00Z", "isExhausted": false },
          "maxTokens": 64000,
          "recommended": true,
          "supportsImages": true,
          "supportsThinking": false,
          "modelProvider": "claude"
        },
        "gemini-3-flash": {
          "displayName": "Gemini 3 Flash",
          "model": "gemini-3-flash",
          "label": "Gemini 3 Flash",
          "quotaInfo": { "remainingFraction": 1.0, "resetTime": "2026-01-26T14:00:00Z", "isExhausted": false },
          "modelProvider": "google"
        }
      },
      "defaultAgentModelId": "claude-sonnet-4-5"
    }
    """;

    private const string QuotaSummaryPayload = """
    {
      "groups": [
        {
          "displayName": "5-hour window",
          "description": "Rolling 5-hour quota",
          "buckets": [
            { "bucketId": "b1", "window": "5h", "remainingFraction": 0.5, "resetTime": "2026-08-10T15:00:00Z", "displayName": "5h" }
          ]
        },
        {
          "displayName": "Weekly window",
          "description": "Weekly quota",
          "buckets": [
            { "bucketId": "b2", "window": "7d", "remainingFraction": 0.9, "resetTime": "2026-08-14T00:00:00Z", "displayName": "7d" }
          ]
        }
      ]
    }
    """;

    private static string CredentialJson(bool expired = false)
        => "{\"token\":{\"access_token\":\"" + FakeAccess + "\",\"token_type\":\"Bearer\"," +
           "\"refresh_token\":\"" + FakeRefresh + "\",\"expiry\":\"" +
           (expired ? "2026-01-01T00:00:00Z" : "2030-01-01T00:00:00Z") + "\"},\"auth_method\":\"oauth\"}";

    [Fact]
    public void ParsesAvailableModelsTakingDefaultModelPrimaryWindow()
    {
        var quota = AntigravityUsage.ParseAvailableModels(AvailableModelsPayload);
        Assert.NotNull(quota);

        Assert.NotNull(quota!.Primary5h);
        Assert.Equal(15, quota.Primary5h!.UsedPercent, 3); // (1 - 0.85) * 100
        Assert.Equal(new DateTimeOffset(2026, 1, 26, 12, 0, 0, TimeSpan.Zero), quota.Primary5h.ResetsAt);
        Assert.Equal(WindowKind.FiveHour, quota.Primary5h.WindowKind);
    }

    [Fact]
    public void ParsesQuotaSummaryBucketsIntoFiveHourAndSevenDay()
    {
        var quota = AntigravityUsage.ParseQuotaSummary(QuotaSummaryPayload);
        Assert.NotNull(quota);

        Assert.Equal(50, quota!.Primary5h!.UsedPercent, 3);
        Assert.Equal(new DateTimeOffset(2026, 8, 10, 15, 0, 0, TimeSpan.Zero), quota.Primary5h.ResetsAt);
        Assert.Equal(10, quota.Secondary7d!.UsedPercent, 3);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero), quota.Secondary7d.ResetsAt);
    }

    [Theory]
    [InlineData("5h", null, null, WindowKind.FiveHour)]
    [InlineData(null, "Weekly", null, WindowKind.SevenDay)]
    [InlineData("7d", null, null, WindowKind.SevenDay)]
    [InlineData(null, null, "hourly", WindowKind.FiveHour)]
    [InlineData(null, null, "7 days", WindowKind.SevenDay)]
    [InlineData(null, null, "default", WindowKind.Unknown)]
    public void ClassifiesWindows(string? id, string? label, string? window, WindowKind expected)
        => Assert.Equal(expected, AntigravityUsage.ClassifyWindow(id, label, window));

    [Fact]
    public void MalformedPayloadsReturnNull()
    {
        Assert.Null(AntigravityUsage.ParseAvailableModels("not json"));
        Assert.Null(AntigravityUsage.ParseAvailableModels("{}"));
        Assert.Null(AntigravityUsage.ParseQuotaSummary("{\"groups\":[]}"));
    }

    [Fact]
    public async Task FetchUsesQuotaSummaryWhenAvailable()
    {
        var stub = new StubHttpHandler()
            .On("POST", "/v1internal:retrieveUserQuotaSummary", (200, QuotaSummaryPayload));
        using var usage = new AntigravityUsage(stub, credentialReader: () => CredentialJson());

        var data = await usage.FetchAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(data);
        Assert.Null(data!.Error);
        Assert.Equal(50, data.UsedPercent5h!.Value, 3);
        Assert.Equal(10, data.UsedPercent7d!.Value, 3);

        var request = Assert.Single(stub.Requests);
        Assert.Equal("Bearer " + FakeAccess, request.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task FetchFallsBackToAvailableModelsWhenQuotaSummaryFails()
    {
        var stub = new StubHttpHandler()
            .On("POST", "/v1internal:retrieveUserQuotaSummary", (404, "{\"error\":\"nope\"}"))
            .On("POST", "/v1internal:fetchAvailableModels", (200, AvailableModelsPayload));
        using var usage = new AntigravityUsage(stub, credentialReader: () => CredentialJson());

        var data = await usage.FetchAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(data);
        Assert.Null(data!.Error);
        Assert.Equal(15, data.UsedPercent5h!.Value, 3);
        Assert.Contains(stub.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("fetchAvailableModels"));
    }

    [Fact]
    public async Task ExpiredAccessTokenReturnsUnavailableWithoutRefreshRequest()
    {
        var stub = new StubHttpHandler()
            .On("POST", "/v1internal:retrieveUserQuotaSummary", (200, QuotaSummaryPayload));
        using var usage = new AntigravityUsage(stub, credentialReader: () => CredentialJson(expired: true));

        var data = await usage.FetchAsync(ct: TestContext.Current.CancellationToken);

        Assert.NotNull(data);
        Assert.Contains("expired", data!.Error);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task MissingCredentialReturnsFailureWithoutThrowing()
    {
        using var usage = new AntigravityUsage(credentialReader: () => null);
        var data = await usage.FetchAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(data);
        Assert.Contains("credential", data!.Error);
    }

    [Fact]
    public async Task MalformedCredentialReturnsFailureWithoutThrowing()
    {
        using var usage = new AntigravityUsage(credentialReader: () => "not json at all");
        var data = await usage.FetchAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(data);
        Assert.NotNull(data!.Error);
    }

    [Fact]
    public async Task TokenNeverLeaksIntoErrorOnNetworkFailure()
    {
        using var usage = new AntigravityUsage(new NetworkDownHandler(), credentialReader: () => CredentialJson());
        var data = await usage.FetchAsync(ct: TestContext.Current.CancellationToken);
        Assert.NotNull(data);
        Assert.NotNull(data!.Error);
        AssertNoSecret.DoesNotContain(data.Error, FakeAccess);
        AssertNoSecret.DoesNotContain(data.Error, FakeRefresh);
    }
}
