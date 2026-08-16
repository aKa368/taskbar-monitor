using TaskbarMonitor.UI;
using TaskbarMonitor.AgentUsage;
using Xunit;

namespace TaskbarMonitor.Tests.Layout;

public sealed class UsageTextFormatterTests
{
    [Fact]
    public void AgentDisplayIsPendingWhenDataHasNotArrived() =>
        Assert.Equal("--", UsageTextFormatter.FormatAgentDisplay(null, showReset: false));

    [Fact]
    public void AgentDisplayIsUnavailableForSuccessfulDataWithoutValues() =>
        Assert.Equal("N/A", UsageTextFormatter.FormatAgentDisplay(
            new UsageData { Agent = AgentIds.Codex, Source = "api" }, showReset: false));

    [Fact]
    public void AgentDisplayIsErrorWhenFailureHasNoLastKnownValue() =>
        Assert.Equal("ERR", UsageTextFormatter.FormatAgentDisplay(
            UsageData.Failure(AgentIds.Codex, "redacted failure"), showReset: false));

    [Fact]
    public void AgentDisplayKeepsStaleSevenDayValueAndMarksError() =>
        Assert.Equal("7d 80% left · ERR", UsageTextFormatter.FormatAgentDisplay(new UsageData
        {
            Agent = AgentIds.Codex,
            Source = "api",
            UsedPercent7d = 20,
            Error = "redacted failure"
        }, showReset: false));

    [Fact]
    public void ShowsUnavailableRatherThanAFalseZeroPercent()
    {
        Assert.Equal("--", UsageTextFormatter.FormatQuotaPercent(null, showReset: false, resetsAt: null));
    }

    [Fact]
    public void ShowsTheActualChatGptQuotaPercent()
    {
        Assert.Equal("23%", UsageTextFormatter.FormatQuotaPercent(23, showReset: false, resetsAt: null));
    }

    [Fact]
    public void BestQuotaPrefersFiveHourWhenBothExist()
    {
        var selected = UsageTextFormatter.SelectQuotaWindow(12, DateTime.Today.AddHours(1), 20, DateTime.Today.AddDays(1));
        Assert.Equal(12, selected.UsedPercent);
        Assert.Equal("5h", selected.Label);
        Assert.Equal("12%", UsageTextFormatter.FormatBestQuota(12, null, 20, null, showReset: false));
    }

    [Fact]
    public void BestQuotaFallsBackToLabeledSevenDayAndItsReset()
    {
        DateTime fiveHourReset = DateTime.Now.AddHours(1);
        DateTime sevenDayReset = DateTime.Now.AddHours(25);
        var selected = UsageTextFormatter.SelectQuotaWindow(null, fiveHourReset, 20, sevenDayReset);
        Assert.Equal(sevenDayReset, selected.ResetsAt);
        Assert.Equal("7d", selected.Label);
        Assert.Equal($"20% 7d · {UsageTextFormatter.FormatResetTime(sevenDayReset)}",
            UsageTextFormatter.FormatBestQuota(null, fiveHourReset, 20, sevenDayReset, showReset: true));
    }

    [Fact]
    public void SuccessfulApiDataWithoutAWindowIsUnavailable() =>
        Assert.Equal("N/A", UsageTextFormatter.FormatBestQuota(null, null, null, null, showReset: false));

    [Theory]
    [InlineData(0, 0, "5h 100% left · 7d 100% left")]
    [InlineData(100, 100, "5h 0% left · 7d 0% left")]
    [InlineData(23, 6, "5h 77% left · 7d 94% left")]
    public void FormatsRemainingQuotaForBothWindows(double used5h, double used7d, string expected) =>
        Assert.Equal(expected, UsageTextFormatter.FormatRemainingQuota(used5h, used7d));

    [Fact]
    public void RemainingQuotaDoesNotInventMissingFiveHourWindow() =>
        Assert.Equal("7d 80% left", UsageTextFormatter.FormatRemainingQuota(null, 20));

    [Fact]
    public void CompactAccountQuotaFitsSplitHostCards() =>
        Assert.Equal("5h77 · 7d94", UsageTextFormatter.FormatCompactAgentDisplay(new UsageData
        {
            Agent = AgentIds.Codex,
            Source = "api",
            UsedPercent5h = 23,
            UsedPercent7d = 6
        }));

    [Fact]
    public void CompactAccountQuotaKeepsStaleErrorVisible() =>
        Assert.Equal("7d80 · ERR", UsageTextFormatter.FormatCompactAgentDisplay(new UsageData
        {
            Agent = AgentIds.Codex,
            Source = "api",
            UsedPercent7d = 20,
            Error = "redacted"
        }));

    [Theory]
    [InlineData(true, 3, "GPU 3%")]
    [InlineData(false, double.NaN, "")]
    public void FormatsTheSharedGridPerformanceCell(bool gpuEnabled, double gpuPercent, string expected) =>
        Assert.Equal(expected, GridPerformanceTextFormatter.Format(gpuEnabled, gpuPercent, false, double.NaN));
}
