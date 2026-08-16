using Deskband11Lib.Core;
using TaskbarMonitor;
using TaskbarMonitor.Config;
using Xunit;

namespace ConfigTests;

public sealed class TaskbarHostOptionsTests
{
    [Fact]
    public void HostsUseIndependentPermanentPlacements()
    {
        var config = new ConfigData { PreferredWidth = 560, Position = "Center", Placement = "Auto" };
        Assert.Equal(TaskbarContentPlacement.AccountArea, TaskbarHostOptions.CreateAccounts(config).Placement);
        Assert.Equal(TaskbarContentPlacement.BeforeNotificationArea, TaskbarHostOptions.CreateSystem(config).Placement);
    }

    [Theory]
    [InlineData(560, 335.2, 319.2)]
    [InlineData(360, 296, 280)]
    [InlineData(100, 296, 280)]
    public void IndependentWidthsAreContentFirst(double legacyWidth, double expectedAccount, double expectedSystem)
    {
        var config = new ConfigData { PreferredWidth = legacyWidth };
        if (legacyWidth == 560) config.Agents.Codex = true;
        double account = TaskbarHostOptions.AccountWidth(config);
        double system = TaskbarHostOptions.SystemWidth(config);
        Assert.Equal(expectedAccount, account, 6);
        Assert.Equal(expectedSystem, system, 6);
    }

    [Fact]
    public void FiveAgentsReceiveAContentFirstAccountSlot()
    {
        var config = new ConfigData { PreferredWidth = 360 };
        config.Agents.Codex = true;
        config.Agents.Antigravity = true;
        config.Agents.Claude = true;
        Assert.Equal(5, TaskbarHostOptions.CountEnabledAgents(config));
        Assert.Equal(296, TaskbarHostOptions.AccountWidth(config));
    }
}
