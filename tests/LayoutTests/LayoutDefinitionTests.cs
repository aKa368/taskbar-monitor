using TaskbarMonitor.UI.Layout;
using Xunit;

namespace TaskbarMonitor.Tests.Layout;

public class LayoutDefinitionTests
{
    [Fact]
    public void TwoLine_HasTwoRows_AndFitsDefaultTaskbar()
    {
        var def = LayoutDefinition.Get(WidgetLayoutKind.TwoLine);
        Assert.True(def.TwoRow);
        Assert.True(def.MinTaskbarHeight <= 48); // taskbar Win11 mặc định 48px
    }

    [Fact]
    public void TwoLine_DoesNotShowResetCountdown_ByDefault()
    {
        var def = LayoutDefinition.Get(WidgetLayoutKind.TwoLine);
        Assert.False(def.ShowAgentResetText);
    }

    [Fact]
    public void Compact_ShowsResetCountdown()
    {
        var def = LayoutDefinition.Get(WidgetLayoutKind.Compact);
        Assert.True(def.ShowAgentResetText);
    }


    [Theory]
    [InlineData(WidgetLayoutKind.Minimal, true)]   // Minimal vẫn hiện metrics text (không border/chart)
    [InlineData(WidgetLayoutKind.AgentCentric, false)]
    [InlineData(WidgetLayoutKind.Compact, true)]
    [InlineData(WidgetLayoutKind.TwoLine, true)]
    public void Layout_ShowMetricPods_Matches(WidgetLayoutKind kind, bool expected)
    {
        Assert.Equal(expected, LayoutDefinition.Get(kind).ShowMetricPods);
    }

    [Theory]
    [InlineData(WidgetLayoutKind.AgentCentric, true)]
    [InlineData(WidgetLayoutKind.Compact, true)]
    public void Layout_ShowAgentPods_Matches(WidgetLayoutKind kind, bool expected)
    {
        Assert.Equal(expected, LayoutDefinition.Get(kind).ShowAgentPods);
    }

    [Fact]
    public void AllKinds_HaveUniqueNames()
    {
        var names = Enum.GetValues<WidgetLayoutKind>().Select(LayoutDefinition.Get).Select(d => d.Name);
        Assert.Equal(names.Count(), names.Distinct().Count());
    }

    [Fact]
    public void UnknownKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutDefinition.Get((WidgetLayoutKind)999));
    }
}
