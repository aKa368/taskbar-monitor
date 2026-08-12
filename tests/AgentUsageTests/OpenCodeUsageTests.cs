using TaskbarMonitor.AgentUsage;
using Xunit;

namespace AgentUsageTests;

public sealed class OpenCodeUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static long Ms(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    [Fact]
    public void AggregatesCostAndAllTokenColumnsFromFixture()
    {
        var path = TestDb.CreateSessionDb(
        [
            (Ms(Now.AddHours(-2)), 0.50, 1000, 200, 50, 300, 40),   // inside 5h
            (Ms(Now.AddHours(-6)), 1.25, 2000, 400, 100, 600, 80),  // inside 7d only
            (Ms(Now.AddDays(-8)), 9.00, 9999, 9999, 9999, 9999, 9999), // outside 7d
        ]);
        try
        {
            using var usage = new OpenCodeUsage(path);
            var data = Assert.IsType<UsageData>(usage.Read(Now));

            Assert.Equal(AgentIds.OpenCode, data.Agent);
            Assert.Null(data.Error);

            Assert.NotNull(data.Last5h);
            Assert.Equal(0.50, data.Last5h!.Cost!.Value, 6);
            Assert.Equal(1000, data.Last5h.TokensInput);
            Assert.Equal(200, data.Last5h.TokensOutput);
            Assert.Equal(50, data.Last5h.TokensReasoning);
            Assert.Equal(300, data.Last5h.TokensCacheRead);
            Assert.Equal(40, data.Last5h.TokensCacheWrite);
            Assert.Equal(1000 + 200 + 50 + 300 + 40, data.Last5h.TokensTotal);
            Assert.Equal(1, data.Last5h.Requests);

            Assert.NotNull(data.Last7d);
            Assert.Equal(1.75, data.Last7d!.Cost!.Value, 6);
            Assert.Equal(3000, data.Last7d.TokensInput);
            Assert.Equal(600, data.Last7d.TokensOutput);
            Assert.Equal(150, data.Last7d.TokensReasoning);
            Assert.Equal(900, data.Last7d.TokensCacheRead);
            Assert.Equal(120, data.Last7d.TokensCacheWrite);
            Assert.Equal(2, data.Last7d.Requests);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }

    [Fact]
    public void EmptySessionTableYieldsZeroedSnapshot()
    {
        var path = TestDb.CreateSessionDb([]);
        try
        {
            using var usage = new OpenCodeUsage(path);
            var data = Assert.IsType<UsageData>(usage.Read(Now));
            Assert.Equal(0, data.Last5h!.Requests);
            Assert.Equal(0, data.Last7d!.Requests);
            Assert.Equal(0, data.Last7d.TokensTotal);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }

    [Fact]
    public void MissingDatabaseReturnsNullWithoutThrowing()
    {
        using var usage = new OpenCodeUsage("Z:\\definitely\\missing\\opencode.db");
        Assert.False(usage.TryRead(out var data));
        Assert.Null(data);
    }

    [Fact]
    public void CorruptDatabaseReturnsNullWithoutThrowing()
    {
        string path = TestDb.TempFile();
        try
        {
            File.WriteAllText(path, "not a database");
            using var usage = new OpenCodeUsage(path);
            Assert.False(usage.TryRead(out var data));
            Assert.Null(data);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }
}
