using System.ComponentModel;

namespace TaskbarMonitor.UI.Palette;

/// <summary>Loại hành động của một settings command.</summary>
public enum SettingsCommandKind
{
    ToggleMetric,
    ToggleAgent,
    ChooseLayout,
    ChooseDensity,
    SetInterval,
    SetPlacement,
    ToggleAutostart,
    ToggleShowLabels,
    ToggleShowResetCountdown,
    OpenConfigFile,
    Quit
}

/// <summary>
/// Một command trong settings palette — có label/description để search,
/// IsActive() cho trạng thái hiện tại, Execute() sửa config.
/// </summary>
public sealed class SettingsCommand : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required SettingsCommandKind Kind { get; init; }
    public required Action Execute { get; init; }
    public Func<bool>? IsActive { get; init; }
    public Func<string>? GetValueText { get; init; }

    /// <summary>Trạng thái active hiện tại (bind cho checkmark trong palette).</summary>
    public bool IsActiveValue => IsActive?.Invoke() ?? false;

    /// <summary>Text hiển thị cho mục đang active (vd "Layout: TwoLine").</summary>
    public string ValueText => GetValueText?.Invoke() ?? "";

    /// <summary>Báo PropertyChanged để UI refresh checkmark/value (gọi sau khi config đổi).</summary>
    public void NotifyStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActiveValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
