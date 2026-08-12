using TaskbarMonitor.UI;
using Xunit;

namespace TaskbarMonitor.Tests.Layout;

public sealed class NetworkRateTextFormatterTests
{
    [Fact]
    public void FormatsUploadAndDownloadAsOneCompactNetworkCell()
    {
        Assert.Equal("↑512K ↓1.5M", NetworkRateTextFormatter.FormatPair(512, 1536));
    }

    [Fact]
    public void CapsExtremeRatesToTheFixedWidthDisplay()
    {
        Assert.Equal("↑999G+ ↓999G+", NetworkRateTextFormatter.FormatPair(float.MaxValue, float.MaxValue));
    }

    [Fact]
    public void FormatsUnavailableRatesWithoutFabricatingAValue()
    {
        Assert.Equal("↑ --", NetworkRateTextFormatter.FormatUpload(float.NaN));
        Assert.Equal("↓ --", NetworkRateTextFormatter.FormatDownload(float.PositiveInfinity));
    }
}
