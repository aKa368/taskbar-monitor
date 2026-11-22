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
        var configSide = new ConfigData { PreferredWidth = 560, Position = "Side", Placement = "Auto" };
        Assert.Equal(TaskbarContentPlacement.AccountArea, TaskbarHostOptions.CreateAccounts(configSide).Placement);
        Assert.Equal(TaskbarContentPlacement.BeforeNotificationArea, TaskbarHostOptions.CreateSystem(configSide).Placement);

        var configCenter = new ConfigData { PreferredWidth = 560, Position = "Center", Placement = "Auto" };
        Assert.Equal(TaskbarContentPlacement.BeforeStartButton, TaskbarHostOptions.CreateAccounts(configCenter).Placement);
        Assert.Equal(TaskbarContentPlacement.AfterTaskbarButtons, TaskbarHostOptions.CreateSystem(configCenter).Placement);
    }

    [Theory]
    [InlineData(560, 590, 560)]
    [InlineData(360, 480, 500)]
    [InlineData(100, 480, 500)]
    public void IndependentWidthsAreContentFirst(double legacyWidth, double expectedAccount, double expectedSystem)
    {
        var config = new ConfigData { PreferredWidth = legacyWidth };
        if (legacyWidth == 560) config.Agents.Antigravity = true;
        double account = TaskbarHostOptions.AccountWidth(config);
        double system = TaskbarHostOptions.SystemWidth(config);
        Assert.Equal(expectedAccount, account, 6);
        Assert.Equal(expectedSystem, system, 6);
    }

    [Fact]
    public void FiveAgentsReceiveAContentFirstAccountSlot()
    {
        var config = new ConfigData { PreferredWidth = 360 };
        config.Agents.CommandCode = true;
        config.Agents.OpenCode = true;
        config.Agents.Antigravity = true;
        config.Agents.Claude = true;
        Assert.Equal(4, TaskbarHostOptions.CountEnabledAgents(config));
        Assert.True(TaskbarHostOptions.AccountWidth(config) >= 700);
    }

    [Fact]
    public void LargeFontScalesSlotWidth()
    {
        var configNormal = new ConfigData { FontSize = 11.5 };
        var configLarge = new ConfigData { FontSize = 14.5 };
        var configVeryLarge = new ConfigData { FontSize = 16.0 };

        double sysNormal = TaskbarHostOptions.SystemWidth(configNormal);
        double sysLarge = TaskbarHostOptions.SystemWidth(configLarge);
        double sysVeryLarge = TaskbarHostOptions.SystemWidth(configVeryLarge);

        Assert.True(sysLarge > sysNormal);
        Assert.True(sysVeryLarge > sysLarge);

        double accNormal = TaskbarHostOptions.AccountWidth(configNormal);
        double accLarge = TaskbarHostOptions.AccountWidth(configLarge);
        double accVeryLarge = TaskbarHostOptions.AccountWidth(configVeryLarge);

        Assert.True(accLarge > accNormal);
        Assert.True(accVeryLarge > accLarge);
    }
}