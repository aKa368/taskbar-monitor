using System.ComponentModel;
using System.Runtime.CompilerServices;


namespace TaskbarMonitor.UI.Layout;

/// <summary>Loại pod hiển thị trong widget.</summary>
public enum PodKind
{
    Metric,
    Agent
}

/// <summary>
/// ViewModel cho một pod (metric hoặc agent) trong widget.
/// LayoutManager build danh sách pod theo layout; Tick() chỉ update value.
/// </summary>
public abstract class PodViewModel : INotifyPropertyChanged
{
    private string _key = "";
    private string _label = "";
    private string _valueText = "";
    private bool _isVisible = true;
    private string _toolTipText = "";

    public PodKind Kind { get; protected init; }

    /// <summary>Key định danh: "cpu", "ram", "CommandCode", ...</summary>
    public string Key
    {
        get => _key;
        set => SetField(ref _key, value);
    }

    /// <summary>Label hiển thị: "CPU", "RAM", "CC", ...</summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    /// <summary>Text giá trị chính: "CPU 12%", "↑12K ↓2.1M", "42% · reset in 03:12", ...</summary>
    public string ValueText
    {
        get => _valueText;
        set => SetField(ref _valueText, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    public string ToolTipText
    {
        get => _toolTipText;
        set => SetField(ref _toolTipText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>Pod metric: text-only compact metric.</summary>
public sealed class MetricPodViewModel : PodViewModel
{
    public MetricPodViewModel(string key, string label)
    {
        Kind = PodKind.Metric;
        Key = key;
        Label = label;
    }

}

/// <summary>Pod agent usage: percentage text + reset text.</summary>
public sealed class AgentPodViewModel : PodViewModel
{
    private string _fiveHourResetText = "";
    private string _sevenDayResetText = "";
    private double _fiveHourPercentage;
    private double _sevenDayPercentage;
    private string _colorHex = "#6F42C1";

    public AgentPodViewModel(string key, string label)
    {
        Kind = PodKind.Agent;
        Key = key;
        Label = label;
    }


    public string FiveHourResetText
    {
        get => _fiveHourResetText;
        set => SetField(ref _fiveHourResetText, value);
    }

    public string SevenDayResetText
    {
        get => _sevenDayResetText;
        set => SetField(ref _sevenDayResetText, value);
    }

    public double FiveHourPercentage
    {
        get => _fiveHourPercentage;
        set => SetField(ref _fiveHourPercentage, value);
    }

    public double SevenDayPercentage
    {
        get => _sevenDayPercentage;
        set => SetField(ref _sevenDayPercentage, value);
    }

    public string ColorHex
    {
        get => _colorHex;
        set => SetField(ref _colorHex, value);
    }
}
