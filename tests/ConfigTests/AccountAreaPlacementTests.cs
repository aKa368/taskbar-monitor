using Deskband11Lib.Core;
using Deskband11Lib.Core.Internal;
using Windows.Win32.Foundation;
using Xunit;

namespace ConfigTests;

public sealed class AccountAreaPlacementTests
{
    [Theory]
    [InlineData(TaskbarAlignment.Left, TaskbarAlignment.Center)]
    [InlineData(TaskbarAlignment.Center, TaskbarAlignment.Left)]
    public void LiveStartGeometryOverridesConflictingRegistry(TaskbarAlignment registry, TaskbarAlignment expected)
    {
        var taskbar = new RECT { left = 0, right = 1920, top = 1032, bottom = 1080 };
        var start = expected == TaskbarAlignment.Center ? new ButtonSpan(870, 918) : new ButtonSpan(0, 48);

        var decision = TaskbarAlignmentDetector.Detect(taskbar, start, registry);

        Assert.Equal(expected, decision.Alignment);
        Assert.Equal(AlignmentSource.LiveGeometry, decision.Source);
    }

    [Fact]
    public void ScreenshotLikeCenteredGeometryKeepsAccountPhysicallyBeforeStart()
    {
        var taskbar = new RECT { left = 0, right = 1920, top = 1032, bottom = 1080 };
        var geometry = new TaskbarButtonGeometry(
            new ButtonSpan(816, 864),
            ButtonSpan.Invalid,
            new ButtonSpan(864, 1200),
            new ButtonSpan(1660, 1920));
        var decision = TaskbarAlignmentDetector.Detect(taskbar, geometry.StartButton, TaskbarAlignment.Left);

        var area = TaskbarLayoutCalculator.SelectContentArea(
            TaskbarContentPlacement.AccountArea, decision.Alignment, taskbar, 1660, geometry);

        Assert.Equal(AlignmentSource.LiveGeometry, decision.Source);
        Assert.Equal(0, area.AreaLeft);
        Assert.True(area.AreaRight <= geometry.StartButton.Left);
        Assert.True(area.LeftAlign);
    }

    [Fact]
    public void MissingLiveStartGeometryCollapsesAccountInsteadOfRoutingRight()
    {
        var taskbar = new RECT { left = 0, right = 1920, top = 1032, bottom = 1080 };
        var geometry = new TaskbarButtonGeometry(
            ButtonSpan.Invalid, ButtonSpan.Invalid, ButtonSpan.Invalid, new ButtonSpan(1660, 1920));

        var area = TaskbarLayoutCalculator.SelectContentArea(
            TaskbarContentPlacement.AccountArea, TaskbarAlignment.Unknown, taskbar, 1660, geometry);

        Assert.Equal(taskbar.left, area.AreaLeft);
        Assert.Equal(taskbar.left, area.AreaRight);
    }

    [Fact]
    public void MissingUiaUsesVerifiedExplorerContentBoundaryOnCenteredTaskbar()
    {
        var taskbar = new RECT { left = 0, right = 1920, top = 1032, bottom = 1080 };
        var explorerContent = new RECT { left = 762, right = 1114, top = 1032, bottom = 1080 };
        Assert.True(TaskbarLayoutCalculator.IsSafeNativeLeftBoundary(taskbar, explorerContent, 1706));

        var area = TaskbarLayoutCalculator.SelectContentArea(
            TaskbarContentPlacement.AccountArea, TaskbarAlignment.Center, taskbar, 1706,
            default, nativeSafeLeftBoundary: explorerContent.left);

        Assert.Equal(0, area.AreaLeft);
        Assert.Equal(762, area.AreaRight);
        Assert.True(area.LeftAlign);
    }

    [Theory]
    [InlineData(0, 500, 1706)]
    [InlineData(1800, 1900, 1706)]
    public void UnsafeExplorerBoundaryCannotMakeAccountVisible(int explorerLeft, int explorerRight, int notificationLeft)
    {
        var taskbar = new RECT { left = 0, right = 1920, top = 1032, bottom = 1080 };
        var explorer = new RECT { left = explorerLeft, right = explorerRight, top = 1032, bottom = 1080 };
        Assert.False(TaskbarLayoutCalculator.IsSafeNativeLeftBoundary(taskbar, explorer, notificationLeft));
    }

    [Fact]
    public void GeometryCacheKeyInvalidatesForHwndRectangleOrMonitorChanges()
    {
        var original = new RECT { left = 0, top = 1000, right = 1920, bottom = 1080 };
        var moved = new RECT { left = -2560, top = 1360, right = 0, bottom = 1440 };

        Assert.True(TaskbarButtonReader.SameCacheKey((nint)10, original, 0, (nint)10, original, 0));
        Assert.False(TaskbarButtonReader.SameCacheKey((nint)10, original, 0, (nint)11, original, 0));
        Assert.False(TaskbarButtonReader.SameCacheKey((nint)10, original, 0, (nint)10, moved, 0));
        Assert.False(TaskbarButtonReader.SameCacheKey((nint)10, original, 0, (nint)10, original, 1));
    }

    [Fact]
    public void LeftAlignedAccountAreaStartsAfterWholeInteractiveGroup()
    {
        var taskbar = new RECT { left = 1920, right = 4480, top = 1400, bottom = 1440 };
        var geometry = new TaskbarButtonGeometry(
            new ButtonSpan(1920, 1968),
            ButtonSpan.Invalid,
            new ButtonSpan(1968, 2376),
            new ButtonSpan(4200, 4480));

        var area = TaskbarLayoutCalculator.SelectContentArea(
            TaskbarContentPlacement.AccountArea,
            TaskbarAlignment.Left,
            taskbar,
            notificationLeft: 4200,
            geometry);

        Assert.Equal(2376, area.AreaLeft);
        Assert.Equal(4200, area.AreaRight);
        Assert.True(area.LeftAlign);
    }

    [Fact]
    public void CenteredAccountAreaUsesSafeGapAfterWidgetsBeforeStart()
    {
        var taskbar = new RECT { left = -2560, right = 0, top = 0, bottom = 60 };
        var geometry = new TaskbarButtonGeometry(
            new ButtonSpan(-1320, -1272),
            new ButtonSpan(-2560, -2440),
            new ButtonSpan(-1272, -900),
            new ButtonSpan(-280, 0));

        var area = TaskbarLayoutCalculator.SelectContentArea(
            TaskbarContentPlacement.AccountArea,
            TaskbarAlignment.Center,
            taskbar,
            notificationLeft: -280,
            geometry);

        Assert.Equal(-2440, area.AreaLeft);
        Assert.Equal(-1320, area.AreaRight);
        Assert.True(area.LeftAlign);
    }

    [Fact]
    public void LeftAndRightHostsShareOneBudgetAndRemainDisjoint()
    {
        var account = Slot(1, 240, TaskbarContentPlacement.AccountArea);
        var system = Slot(2, 320, TaskbarContentPlacement.BeforeNotificationArea);

        var accountAllocation = TaskbarLayoutCalculator.AllocateWidth(account, [system], 400, 1, oppositeEdges: true);
        var systemAllocation = TaskbarLayoutCalculator.AllocateWidth(system, [account], 400, 1, oppositeEdges: true);
        double accountLeft = 0;
        double accountRight = accountLeft + accountAllocation.Width;
        double systemRight = 400;
        double systemLeft = systemRight - systemAllocation.Width;

        Assert.Equal(400, accountAllocation.Width + systemAllocation.Width);
        Assert.InRange(accountLeft, 0, 400);
        Assert.InRange(systemRight, 0, 400);
        Assert.True(accountRight <= systemLeft);
    }

    [Fact]
    public void GrowingButtonGroupShrinksBothEdgesWithoutCollision()
    {
        var account = Slot(1, 240, TaskbarContentPlacement.AccountArea);
        var system = Slot(2, 320, TaskbarContentPlacement.BeforeNotificationArea);

        var accountAllocation = TaskbarLayoutCalculator.AllocateWidth(account, [system], 260, 1, oppositeEdges: true);
        var systemAllocation = TaskbarLayoutCalculator.AllocateWidth(system, [account], 260, 1, oppositeEdges: true);

        Assert.Equal(260, accountAllocation.Width + systemAllocation.Width);
        Assert.True(accountAllocation.Width <= 240);
        Assert.True(systemAllocation.Width <= 320);
        Assert.True(accountAllocation.Width <= 260 - systemAllocation.Width);
    }

    [Fact]
    public void LeftAlignedPhysicalAreaIncludesOppositeEdgeHostOnlyOnSameMonitor()
    {
        var account = Slot(1, 240, TaskbarContentPlacement.AccountArea);
        var system = Slot(2, 320, TaskbarContentPlacement.BeforeNotificationArea);
        var otherMonitor = new TaskbarSlotInfo(3, 0, 100, TaskbarContentPlacement.BeforeNotificationArea, 8, (nint)3, true);

        var siblings = TaskbarLayoutCalculator.FilterSiblingsInSameArea(
            [system, otherMonitor], account.ActualPlacement, monitorIdentity: 7, sharesRightGapWithAccountArea: true);

        Assert.Single(siblings);
        Assert.Equal(system.WindowHandle, siblings[0].WindowHandle);
    }

    private static TaskbarSlotInfo Slot(ushort index, double width, TaskbarContentPlacement placement) =>
        new(index, 0, width, placement, 7, (nint)index, true);
}
