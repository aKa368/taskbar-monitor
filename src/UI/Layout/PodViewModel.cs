using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

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
    private string _line1 = "";
    private string _line2 = "";
    private string _line3 = "";
    private bool _hasLine2;
    private bool _isVisible = true;
    private string _toolTipText = "";

    public static readonly Brush NormalForegroundBrush = FreezeSolidBrush(Color.FromRgb(0xF0, 0xF4, 0xF8));
    public static readonly Brush WarningForegroundBrush = FreezeSolidBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    public static readonly Brush DangerForegroundBrush = FreezeSolidBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    private static Brush FreezeSolidBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Brush _foregroundBrush = NormalForegroundBrush;
    private string _foregroundHex = "#F0F4F8";

    public Brush ForegroundBrush
    {
        get => _foregroundBrush;
        set => SetField(ref _foregroundBrush, value);
    }

    public string ForegroundHex
    {
        get => _foregroundHex;
        set => SetField(ref _foregroundHex, value);
    }

    public void SetAlertState(bool isDanger, bool isWarning)
    {
        if (isDanger)
        {
            ForegroundBrush = DangerForegroundBrush;
            ForegroundHex = "#EF4444";
        }
        else if (isWarning)
        {
            ForegroundBrush = WarningForegroundBrush;
            ForegroundHex = "#F59E0B";
        }
        else
        {
            ForegroundBrush = NormalForegroundBrush;
            ForegroundHex = "#F0F4F8";
        }
    }

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

    /// <summary>Dòng 1 trong chế độ taskbar dọc (tiêu đề/giá trị chính).</summary>
    public string Line1
    {
        get => _line1;
        set => SetField(ref _line1, value);
    }

    /// <summary>Dòng 2 trong chế độ taskbar dọc (thông số phụ/nhiệt độ).</summary>
    public string Line2
    {
        get => _line2;
        set => SetField(ref _line2, value);
    }

    /// <summary>Dòng 3 trong chế độ taskbar dọc (số lượt call hoặc countdown).</summary>
    public string Line3
    {
        get => _line3;
        set => SetField(ref _line3, value);
    }

    public bool HasLine2
    {
        get => _hasLine2;
        set => SetField(ref _hasLine2, value);
    }

    /// <summary>Khóa độ rộng tối thiểu cho từng pod để tránh giật giao diện khi giá trị thay đổi.</summary>
    public double MinWidth => Key switch
        {
            "cpu_gpu_ram" => 140,
            "cpu_gpu" => 108,
            "ram" => 36,
            "network" => 110,
            "cpu" => 55,
            "gpu" => 55,
            "fps" => 60,
            "ping" => 60,
            "disk" => 55,
            "clock" => 50,
            _ => 0
        };

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
    public static readonly Brush DefaultLabelBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xA0, 0xB0, 0xC4)));
    public static readonly Brush OnlineStatusBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
    public static readonly Brush OfflineStatusBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));

    private static Brush FreezeBrush(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private string _fiveHourResetText = "";
    private string _sevenDayResetText = "";
    private double _fiveHourPercentage;
    private double _sevenDayPercentage;
    private string _colorHex = "#6F42C1";
    private Brush _statusBrush = DefaultLabelBrush;
    private string _statusColor = "#A0B0C4";
    private bool _isOnline;
    private string _horizontalValueText = "";

    public AgentPodViewModel(string key, string label)
    {
        Kind = PodKind.Agent;
        Key = key;
        Label = label;
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set => SetField(ref _statusBrush, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetField(ref _statusColor, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        set => SetField(ref _isOnline, value);
    }

    public string HorizontalValueText
    {
        get => string.IsNullOrEmpty(_horizontalValueText) ? ValueText : _horizontalValueText;
        set => SetField(ref _horizontalValueText, value);
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