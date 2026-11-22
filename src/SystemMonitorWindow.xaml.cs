using System.Windows;
using Deskband11Lib.Wpf;
using Deskband11Lib.Core;
using TaskbarMonitor.Config;
using TaskbarMonitor.UI;

namespace TaskbarMonitor;

/// <summary>Independent system-monitor HWND pinned immediately before the notification area.</summary>
public partial class SystemMonitorWindow : Window
{
    private readonly TaskbarContentHostOptions _hostOptions;
    private readonly TaskbarContentViewModel _viewModel;
    private bool _isClosing;
    public TaskbarContentHost TaskbarContentHost { get; }

    public SystemMonitorWindow(TaskbarContentViewModel viewModel)
    {
        InitializeComponent();
        TrayOnlyWindowStyles.Apply(this);
        _viewModel = viewModel;
        SystemHost.Content = new SystemTaskbarContent(viewModel);
        _hostOptions = TaskbarHostOptions.CreateSystem(ConfigManager.Instance.Config);
        TaskbarContentHost = new TaskbarContentHost(this, Root, _hostOptions);
        Root.SizeChanged += (_, e) => _viewModel.IsVertical = e.NewSize.Height > e.NewSize.Width;
        ConfigManager.Instance.ConfigReloaded += OnConfigReloaded;
        viewModel.LayoutChanged += OnLayoutChanged;
    }

    public Task PrepareTaskbarContentAsync() => TaskbarContentHost.AttachWhenLayoutReadyAsync();
    private void OnConfigReloaded(object? sender, ConfigData config) => Dispatcher.BeginInvoke(() =>
    {
        if (_isClosing) return;
        try
        {
            _hostOptions.PreferredWidth = TaskbarHostOptions.SystemWidth(config);
            _hostOptions.Placement = TaskbarHostOptions.SystemPlacement(config);
            TaskbarContentHost.RefreshLayout();
        }
        catch (ObjectDisposedException) { }
    });

    private void OnLayoutChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            if (_isClosing) return;
            try
            {
                TaskbarContentHost.RefreshLayout();
            }
            catch (ObjectDisposedException) { }
        }));
    protected override void OnClosed(EventArgs e)
    {
        if (!_isClosing)
        {
            _isClosing = true;
            ConfigManager.Instance.ConfigReloaded -= OnConfigReloaded;
            _viewModel.LayoutChanged -= OnLayoutChanged;
            TaskbarContentHost.Dispose();
        }
        base.OnClosed(e);
    }
}
