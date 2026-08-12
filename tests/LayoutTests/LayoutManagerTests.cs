using TaskbarMonitor.Config;
using TaskbarMonitor.UI.Layout;
using Xunit;

namespace TaskbarMonitor.Tests.Layout;

public class LayoutManagerTests
{
    private static ConfigData MakeConfig(WidgetLayoutKind kind)
    {
        var cfg = new ConfigData { Layout = kind.ToString() };
        return cfg;
    }

    [Fact]
    public void LayoutManager_Minimal_HasTextMetricPods()
    {
        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.Minimal, MakeConfig(WidgetLayoutKind.Minimal));

        Assert.NotEmpty(manager.Pods);
        Assert.All(manager.Pods.OfType<MetricPodViewModel>(), p => Assert.NotNull(p.ValueText));
    }

    [Fact]
    public void LayoutManager_Compact_HasTextMetricPods()
    {
        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.Compact, MakeConfig(WidgetLayoutKind.Compact));

        Assert.Contains(manager.Pods, p => p is MetricPodViewModel);
        Assert.All(manager.Pods.OfType<MetricPodViewModel>(), p => Assert.NotNull(p.ValueText));
    }

    [Fact]
    public void LayoutManager_AgentCentric_ExcludesMetrics()
    {
        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.AgentCentric, MakeConfig(WidgetLayoutKind.AgentCentric));

        Assert.DoesNotContain(manager.Pods, p => p is MetricPodViewModel);
        Assert.Contains(manager.Pods, p => p is AgentPodViewModel);
    }

    [Fact]
    public void LayoutManager_TwoLine_SplitsIntoTwoRows()
    {
        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.TwoLine, MakeConfig(WidgetLayoutKind.TwoLine));

        Assert.Equal(2, manager.Rows.Count);
        Assert.All(manager.Rows[0], p => Assert.IsType<MetricPodViewModel>(p));
        Assert.All(manager.Rows[1], p => Assert.IsType<AgentPodViewModel>(p));
    }

    [Fact]
    public void LayoutManager_Apply_SameLayout_DoesNotRebuild()
    {
        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.Compact, MakeConfig(WidgetLayoutKind.Compact));
        var firstPods = manager.Pods;

        manager.Apply(WidgetLayoutKind.Compact, MakeConfig(WidgetLayoutKind.Compact));
        Assert.Same(firstPods, manager.Pods); // cache hit — không rebuild
    }

    [Fact]
    public void LayoutManager_Apply_DifferentLayout_Rebuilds()
    {
        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.Compact, MakeConfig(WidgetLayoutKind.Compact));
        var firstPod = manager.Pods[0];

        manager.Apply(WidgetLayoutKind.Minimal, MakeConfig(WidgetLayoutKind.Minimal));
        Assert.DoesNotContain(firstPod, manager.Pods); // pod cũ bị thay bằng pod mới
    }

    [Fact]
    public void LayoutManager_DisabledAgents_Excluded()
    {
        var cfg = MakeConfig(WidgetLayoutKind.Compact);
        cfg.Agents.CommandCode = false;
        cfg.Agents.OpenCode = false;

        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.Compact, cfg);

        Assert.DoesNotContain(manager.Pods, p => p is AgentPodViewModel { Key: "CommandCode" });
        Assert.DoesNotContain(manager.Pods, p => p is AgentPodViewModel { Key: "OpenCode" });
    }

    [Fact]
    public void LayoutManager_Grid_UsesRequestedMetricAndAgentOrder()
    {
        var cfg = MakeConfig(WidgetLayoutKind.Grid);
        cfg.Metrics.Gpu = true;
        cfg.Metrics.Temperature = true;
        cfg.Agents.Codex = true;
        cfg.Agents.CommandCode = true;

        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.Grid, cfg);

        Assert.Equal(new[] { "cpu", "ram", "gpu", "network" },
            manager.Rows[0].OfType<MetricPodViewModel>().Select(p => p.Key));
        Assert.Equal(new[] { "Codex", "OpenCode", "CommandCode" },
            manager.Rows[1].OfType<AgentPodViewModel>().Select(p => p.Key));
        Assert.Equal("GPT", manager.Rows[1].OfType<AgentPodViewModel>()
            .Single(p => p.Key == "Codex").Label);
    }

    [Fact]
    public void LayoutManager_Grid_PutsNetworkLastWhenOptionalDiskIsEnabled()
    {
        var cfg = MakeConfig(WidgetLayoutKind.Grid);
        cfg.Metrics.Gpu = true;
        cfg.Metrics.Disk = true;

        var manager = new LayoutManager();
        manager.Apply(WidgetLayoutKind.Grid, cfg);

        Assert.Equal(new[] { "cpu", "ram", "gpu", "disk", "network" },
            manager.Rows[0].OfType<MetricPodViewModel>().Select(p => p.Key));
    }
}
