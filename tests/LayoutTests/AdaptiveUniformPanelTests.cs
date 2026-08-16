using TaskbarMonitor.UI;
using Xunit;

namespace LayoutTests;

public sealed class AdaptiveUniformPanelTests
{
    [Theory]
    [InlineData(1, 155, 145, 1, 1)]
    [InlineData(2, 155, 145, 2, 1)]
    [InlineData(3, 320, 96, 1, 3)]
    [InlineData(6, 320, 96, 2, 3)]
    [InlineData(6, 220, 96, 2, 3)]
    public void GridAdaptsRowsToCountAndAllocatedWidth(int count, double width, double minimum, int rows, int columns)
    {
        Assert.Equal((rows, columns), AdaptiveUniformPanel.CalculateGrid(count, width, minimum, 2));
    }

    [Fact]
    public void SystemFourPodsUseRequestedRowsAtInstalledWidthWithoutOverlap()
    {
        string?[] keys = ["cpu", "ram", "gpu", "network"];
        var cells = AdaptiveUniformPanel.PlanPlacements(keys, 2, 2, "network");

        Assert.Equal((0, 0), cells[0]); // CPU top-left
        Assert.Equal((1, 0), cells[1]); // RAM bottom-left
        Assert.Equal((0, 1), cells[2]); // GPU top-right
        Assert.Equal((1, 1), cells[3]); // Network bottom-right
        Assert.Equal(158.5, AdaptiveUniformPanel.CellWidth(320, 2, 3));
        Assert.Equal(4, cells.Distinct().Count());
    }

    [Fact]
    public void NetworkStaysOnSecondRowForMetricSubsets()
    {
        string?[] keys = ["cpu", "ram", "network"];
        var cells = AdaptiveUniformPanel.PlanPlacements(keys, 2, 2, "network");

        Assert.Equal(1, cells[2].Row);
        Assert.Equal(3, cells.Distinct().Count());
    }

    [Fact]
    public void GpuStaysOnTopWhenRamIsDisabled()
    {
        string?[] keys = ["cpu", "gpu", "network"];
        var cells = AdaptiveUniformPanel.PlanPlacements(keys, 2, 2, "network");

        Assert.Equal((0, 0), cells[0]);
        Assert.Equal((0, 1), cells[1]);
        Assert.Equal((1, 1), cells[2]);
    }

    [Fact]
    public void AccountThreePodsAreBoundedAtInstalledWidth()
    {
        string?[] keys = ["CommandCode", "OpenCode", "Codex"];
        var cells = AdaptiveUniformPanel.PlanPlacements(keys, 2, 2, null);

        Assert.Equal(118.5, AdaptiveUniformPanel.CellWidth(240, 2, 3));
        Assert.Equal(3, cells.Distinct().Count());
        Assert.All(cells, cell => Assert.InRange(cell.Column, 0, 1));
        Assert.All(cells, cell => Assert.InRange(cell.Row, 0, 1));
    }

    [Theory]
    [InlineData(155, 76)]
    [InlineData(240, 118.5)]
    [InlineData(280, 138.5)]
    [InlineData(320, 158.5)]
    public void ThreeAccountCardsNeverOverlapAcrossRealWidths(double width, double expectedCellWidth)
    {
        var cells = AdaptiveUniformPanel.PlanPlacements(["CommandCode", "OpenCode", "Codex"], 2, 2, null);
        Assert.Equal(expectedCellWidth, AdaptiveUniformPanel.CellWidth(width, 2, 3));
        Assert.Equal(cells.Count, cells.Distinct().Count());
    }
}
