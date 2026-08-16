using TaskbarMonitor.UI;
using Xunit;

namespace TaskbarMonitor.Tests.Layout;

public sealed class GridPerformanceTextFormatterTests
{
    [Fact]
    public void FormatsGpuTemperature() =>
        Assert.Equal("GPU 3% · 54°C", GridPerformanceTextFormatter.Format(true, 3, true, 54));

    [Fact]
    public void ShowsUnavailableTemperatureHonestly() =>
        Assert.Equal("GPU 3% · --°C", GridPerformanceTextFormatter.Format(true, 3, true, double.NaN));

    [Fact]
    public void IsEmptyWhenGpuIsDisabled() =>
        Assert.Equal(string.Empty, GridPerformanceTextFormatter.Format(false, double.NaN, true, 54));
}
