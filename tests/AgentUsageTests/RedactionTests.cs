using TaskbarMonitor.AgentUsage;
using Xunit;

namespace AgentUsageTests;

public sealed class RedactionTests
{
    private const string Secret = "very-sensitive-access-token-abcdef1234567890";

    [Fact]
    public void ApplyReplacesEveryOccurrenceOfSecret()
    {
        string result = Redact.Apply($"unauthorized with token {Secret} at {Secret}", Secret);
        Assert.DoesNotContain(Secret, result);
        Assert.Equal("unauthorized with token [REDACTED] at [REDACTED]", result);
    }

    [Fact]
    public void ApplyIgnoresShortSecretsToAvoidOverRedaction()
    {
        string result = Redact.Apply("ok token=x", "x");
        Assert.Equal("ok token=x", result);
    }

    [Fact]
    public void ApplyHandlesNullAndEmpty()
    {
        Assert.Equal("[REDACTED]", Redact.Apply(null));
        Assert.Equal("[REDACTED]", Redact.Apply(""));
        Assert.Equal("plain message", Redact.Apply("plain message", "irrelevant"));
    }

    [Fact]
    public void ContainsAnySecretDetectsLeaks()
    {
        Assert.True(Redact.ContainsAnySecret($"leak {Secret} here", Secret));
        Assert.False(Redact.ContainsAnySecret("clean text", Secret));
        Assert.False(Redact.ContainsAnySecret(null, Secret));
    }

    [Fact]
    public void UsageDataFailureRedactsItsInput()
    {
        var data = UsageData.Failure(AgentIds.Codex, $"boom: {Secret}", DateTimeOffset.UnixEpoch, Secret);
        Assert.DoesNotContain(Secret, data.Error);
        Assert.Contains("[REDACTED]", data.Error);
    }

    [Fact]
    public void MultipleSecretsAreAllRedacted()
    {
        string other = "second-secret-0987654321";
        string result = Redact.Apply($"a {Secret} b {other} c", Secret, other);
        Assert.DoesNotContain(Secret, result);
        Assert.DoesNotContain(other, result);
        Assert.Contains("[REDACTED]", result);
    }
}
