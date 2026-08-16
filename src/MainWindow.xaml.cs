using System.Windows;
using Deskband11Lib.Core;
using Deskband11Lib.Wpf;
using TaskbarMonitor.Config;
using TaskbarMonitor.UI;

namespace TaskbarMonitor;

/// <summary>Independent account-usage HWND pinned to the taskbar's left edge.</summary>
public partial class MainWindow : Window
{
    private readonly TaskbarContentHostOptions _hostOptions;
    private readonly TaskbarContentViewModel _viewModel;
    private bool _isClosing;
    public TaskbarContentHost TaskbarContentHost { get; }

    public MainWindow(TaskbarContentViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        AccountHost.Content = new AccountTaskbarContent(viewModel);
        _hostOptions = TaskbarHostOptions.CreateAccounts(ConfigManager.Instance.Config);
        TaskbarContentHost = new TaskbarContentHost(this, Root, _hostOptions);
        ConfigManager.Instance.ConfigReloaded += OnConfigReloaded;
        viewModel.LayoutChanged += OnLayoutChanged;
    }

    public Task PrepareTaskbarContentAsync() => TaskbarContentHost.AttachWhenLayoutReadyAsync();

    private void OnConfigReloaded(object? sender, ConfigData config) => Dispatcher.BeginInvoke(() =>
    {
        _hostOptions.PreferredWidth = TaskbarHostOptions.AccountWidth(config);
        TaskbarContentHost.RefreshLayout();
    });

    private void OnLayoutChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.Render, new Action(TaskbarContentHost.RefreshLayout));

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
