using System.Windows.Media;
using TaskbarMonitor.UI.Layout;
using Xunit;

namespace LayoutTests;

public sealed class DynamicAlertColorsTests
{
    [Fact]
    public void PodViewModel_DefaultForeground_IsNormal()
    {
        var pod = new MetricPodViewModel("cpu", "CPU");
        Assert.Equal("#F0F4F8", pod.ForegroundHex);
        Assert.NotNull(pod.ForegroundBrush);
    }

    [Fact]
    public void PodViewModel_SetAlertState_WarningSetsOrange()
    {
        var pod = new MetricPodViewModel("cpu", "CPU");
        pod.SetAlertState(isDanger: false, isWarning: true);

        Assert.Equal("#F59E0B", pod.ForegroundHex);
        Assert.Same(PodViewModel.WarningForegroundBrush, pod.ForegroundBrush);
    }

    [Fact]
    public void PodViewModel_SetAlertState_DangerSetsRed()
    {
        var pod = new MetricPodViewModel("ram", "RAM");
        pod.SetAlertState(isDanger: true, isWarning: false);

        Assert.Equal("#EF4444", pod.ForegroundHex);
        Assert.Same(PodViewModel.DangerForegroundBrush, pod.ForegroundBrush);
    }

    [Fact]
    public void PodViewModel_SetAlertState_NormalRestoresWhite()
    {
        var pod = new MetricPodViewModel("gpu", "GPU");
        pod.SetAlertState(isDanger: true, isWarning: false);
        Assert.Equal("#EF4444", pod.ForegroundHex);

        pod.SetAlertState(isDanger: false, isWarning: false);
        Assert.Equal("#F0F4F8", pod.ForegroundHex);
        Assert.Same(PodViewModel.NormalForegroundBrush, pod.ForegroundBrush);
    }
}
