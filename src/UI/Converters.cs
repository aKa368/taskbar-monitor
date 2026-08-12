using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TaskbarMonitor.UI.Layout;

namespace TaskbarMonitor.UI;

/// <summary>Chọn DataTemplate theo PodKind (Metric vs Agent).</summary>
public sealed class PodTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is PodViewModel pod && container is FrameworkElement fe)
        {
            string key = pod.Kind == PodKind.Agent ? "AgentPodTemplate" : "MetricPodTemplate";
            return fe.FindResource(key) as DataTemplate;
        }
        return base.SelectTemplate(item, container);
    }
}

/// <summary>Inverted boolean → Visibility (dùng cho IsTwoRow ẩn single-row layout).</summary>
public sealed class InvertedBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is Visibility.Visible;
}
