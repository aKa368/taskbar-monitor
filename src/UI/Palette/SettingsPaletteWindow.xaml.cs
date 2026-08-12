using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TaskbarMonitor.Config;

namespace TaskbarMonitor.UI.Palette;

/// <summary>
/// Popup settings palette kiểu command palette (VS Code Ctrl+Shift+P):
/// gõ search → ↑/↓ chọn → Enter execute → Esc/click-ra-ngoài đóng.
/// Window riêng, không timer ngầm — đóng là GC giải phóng (performance).
/// </summary>
public partial class SettingsPaletteWindow : Window
{
    private readonly SettingsPaletteViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    public SettingsPaletteWindow()
    {
        InitializeComponent();
        _viewModel = new SettingsPaletteViewModel(
            ConfigManager.Instance.Config,
            ConfigManager.Instance.Save);

        DataContext = _viewModel;
        ResultsList.SelectedIndex = 0;

        // Refresh checkmarks/value text khi config đổi (hot-reload)
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) =>
        {
            _viewModel.Refresh();
            ResultsList.Items.Refresh();
        };
        _refreshTimer.Start();

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.W when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                ClosePalette();
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Down:
                if (ResultsList.SelectedIndex < ResultsList.Items.Count - 1)
                {
                    ResultsList.SelectedIndex++;
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (ResultsList.SelectedIndex > 0)
                {
                    ResultsList.SelectedIndex--;
                }
                e.Handled = true;
                break;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => ClosePalette();

    private void ClosePalette()
    {
        if (IsVisible)
        {
            Close();
        }
    }

    private void OnResultsMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ExecuteSelected();
    }

    private void ExecuteSelected()
    {
        if (ResultsList.SelectedItem is SettingsCommand cmd)
        {
            cmd.Execute();
            // Command có thể là Quit — window sẽ tự đóng khi app shutdown.
            // Các command khác: giữ palette mở cho user thao tác tiếp.
        }
    }


    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        base.OnClosed(e);
    }
}
