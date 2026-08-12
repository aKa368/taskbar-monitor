using TaskbarMonitor.UI;
using Xunit;

namespace TaskbarMonitor.Tests.Layout;

public sealed class UsageTextFormatterTests
{
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
}