using TaskbarMonitor.UI;
using TaskbarMonitor.UI.Layout;
using Xunit;

namespace ConfigTests;

public sealed class LayoutParserTests
{
    [Theory]
    [InlineData(nameof(WidgetLayoutKind.Compact))]
    [InlineData(nameof(WidgetLayoutKind.Minimal))]
    [InlineData(nameof(WidgetLayoutKind.TwoLine))]
    [InlineData(nameof(WidgetLayoutKind.AgentCentric))]
    [InlineData(nameof(LayoutParserTests))]
    public void LegacyOrInvalidLayoutAlwaysFallsBackToGrid(string layout)
    {
        Assert.Equal(WidgetLayoutKind.Grid, TaskbarContentViewModel.ParseLayoutKind(layout));
    }

    [Fact]
    public void GridLayoutRemainsGrid()
    {
        Assert.Equal(WidgetLayoutKind.Grid, TaskbarContentViewModel.ParseLayoutKind(nameof(WidgetLayoutKind.Grid)));
    }

    [Fact]
    public void NullLayoutFallsBackToGrid()
    {
        Assert.Equal(WidgetLayoutKind.Grid, TaskbarContentViewModel.ParseLayoutKind(null));
    }
}
