using TaskbarMonitor.AgentUsage;
using Xunit;

namespace AgentUsageTests;

public sealed class CommandCodeUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AggregatesFiveHourAndSevenDayWindowsFromFixture()
    {
        var path = TestDb.CreateUsageHistoryDb("commandcode",
        [
            (TestDb.IsoUtc(Now.AddHours(-3)), 100, 20, 1.50, "ok"),
            (TestDb.IsoUtc(Now.AddHours(-6)), 200, 40, 2.50, "ok"),
            (TestDb.IsoUtc(Now.AddDays(-8)), 300, 60, 3.50, "ok"),
        ]);
        try
        {
            using var usage = new CommandCodeUsage(path);
            var data = Assert.IsType<UsageData>(usage.Read(Now));

            Assert.Equal(AgentIds.CommandCode, data.Agent);
            Assert.Equal("sqlite", data.Source);
            Assert.Null(data.Error);

            Assert.NotNull(data.Last5h);
            Assert.Equal(1.50, data.Last5h!.Cost!.Value, 6);
            Assert.Equal(100, data.Last5h.TokensInput);
            Assert.Equal(20, data.Last5h.TokensOutput);
            Assert.Equal(120, data.Last5h.TokensTotal);
            Assert.Equal(1, data.Last5h.Requests);

            Assert.NotNull(data.Last7d);
            Assert.Equal(4.00, data.Last7d!.Cost!.Value, 6);
            Assert.Equal(300, data.Last7d.TokensInput);
            Assert.Equal(60, data.Last7d.TokensOutput);
            Assert.Equal(360, data.Last7d.TokensTotal);
            Assert.Equal(2, data.Last7d.Requests);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }

    [Fact]
    public void FiltersOutOtherProviders()
    {
        var path = TestDb.CreateUsageHistoryDb("opencode",
        [
            (TestDb.IsoUtc(Now.AddHours(-1)), 999, 999, 99.0, "ok"),
        ]);
        try
        {
            using var usage = new CommandCodeUsage(path);
            var data = Assert.IsType<UsageData>(usage.Read(Now));
            Assert.Equal(0, data.Last5h!.Requests);
            Assert.Equal(0, data.Last7d!.Requests);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }

    [Fact]
    public void BoundaryTimestampExactlyAtFiveHoursIsIncluded()
    {
        var path = TestDb.CreateUsageHistoryDb("commandcode",
        [
            (TestDb.IsoUtc(Now.AddHours(-5)), 10, 5, 0.10, "ok"),
            (TestDb.IsoUtc(Now.AddHours(-5).AddMilliseconds(-1)), 20, 10, 0.20, "ok"),
        ]);
        try
        {
            using var usage = new CommandCodeUsage(path);
            var data = Assert.IsType<UsageData>(usage.Read(Now));

            // Exactly at the boundary counts; 1ms before does not.
            Assert.Equal(10, data.Last5h!.TokensInput);
            Assert.Equal(30, data.Last7d!.TokensInput);
            Assert.Equal(2, data.Last7d.Requests);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }

    [Fact]
    public void EmptyHistoryYieldsZeroedSnapshot()
    {
        var path = TestDb.CreateUsageHistoryDb("commandcode", []);
        try
        {
            using var usage = new CommandCodeUsage(path);
            var data = Assert.IsType<UsageData>(usage.Read(Now));
            Assert.Equal(0, data.Last5h!.Requests);
            Assert.Equal(0, data.Last7d!.Requests);
            Assert.Equal(0, data.Last7d.Cost!.Value, 6);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }

    [Fact]
    public void MissingDatabaseReturnsNullWithoutThrowing()
    {
        using var usage = new CommandCodeUsage("Z:\\definitely\\missing\\data.sqlite");
        Assert.False(usage.TryRead(out var data));
        Assert.Null(data);
    }

    [Fact]
    public void CorruptDatabaseReturnsNullWithoutThrowing()
    {
        string path = TestDb.TempFile();
        try
        {
            File.WriteAllText(path, "this is definitely not a sqlite database");
            using var usage = new CommandCodeUsage(path);
            Assert.False(usage.TryRead(out var data));
            Assert.Null(data);
        }
        finally
        {
            TestDb.Delete(path);
        }
    }

    [Theory]
    [InlineData("2026-08-10T09:00:00.682Z", true)]
    [InlineData("2026-08-10 09:00:00", true)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParsesIsoTimestamps(string? raw, bool expected)
    {
        Assert.Equal(expected, CommandCodeUsage.TryParseTimestamp(raw, out _));
    }
}
