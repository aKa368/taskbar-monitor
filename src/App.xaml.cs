using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using TaskbarMonitor.Config;
using TaskbarMonitor.UI;

namespace TaskbarMonitor;

public partial class App : Application
{
    private const string MutexName = "TaskbarMonitor_SingleInstance_Mutex_AGY";
    private static Mutex? _singleInstanceMutex;
    private TaskbarContentViewModel? _viewModel;
    private TaskbarPairLifecycle? _pairLifecycle;
    private FloatingMonitorWindow? _floatingWindow;
    private string _currentMode = string.Empty;
    private TrayIconService? _trayIcon;
    private MemoryTelemetry? _memoryTelemetry;
    private WorkingSetTrimmer? _workingSetTrimmer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _memoryTelemetry = new MemoryTelemetry();
        _workingSetTrimmer = new WorkingSetTrimmer(TimeSpan.FromSeconds(30));

        UpdateAutostartRegistry(ConfigManager.Instance.Config.Autostart);
        _trayIcon = new TrayIconService();
        _viewModel = new TaskbarContentViewModel();

        ApplyMode(ConfigManager.Instance.Config.Mode);

        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(2000);
            WorkingSetTrimmer.TrimWorkingSet();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        ConfigManager.Instance.ConfigReloaded += (_, cfg) =>
        {
            UpdateAutostartRegistry(cfg.Autostart);
            Dispatcher.Invoke(() => ApplyMode(cfg.Mode));
        };
    }

    private void ApplyMode(string mode)
    {
        if (string.Equals(_currentMode, mode, StringComparison.OrdinalIgnoreCase))
            return;
        _currentMode = mode;

        if (string.Equals(mode, "Floating", StringComparison.OrdinalIgnoreCase))
        {
            if (_pairLifecycle != null)
            {
                _pairLifecycle.RequestShutdown();
                _pairLifecycle = null;
            }
            if (_floatingWindow == null && _viewModel != null)
            {
                _floatingWindow = new FloatingMonitorWindow(_viewModel);
                _floatingWindow.Closed += (_, _) => _floatingWindow = null;
                _floatingWindow.Show();
            }
        }
        else
        {
            if (_floatingWindow != null)
            {
                _floatingWindow.Close();
                _floatingWindow = null;
            }
            if (_viewModel != null)
            {
                _pairLifecycle?.RequestShutdown();
                _pairLifecycle = new TaskbarPairLifecycle(() => new TaskbarWindowPair(_viewModel));
                _ = _pairLifecycle.StartAsync();
            }
        }
    }

    private static void UpdateAutostartRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;

            string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (enable)
            {
                key.SetValue("TaskbarMonitor", $"\"{exePath}\"");
            }
            else if (key.GetValue("TaskbarMonitor") != null)
            {
                key.DeleteValue("TaskbarMonitor", false);
            }
        }
        catch
        {
            // Autostart is optional; a registry failure must not kill the monitor.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pairLifecycle?.RequestShutdown();
        _viewModel?.Dispose();
        _trayIcon?.Dispose();
        _workingSetTrimmer?.Dispose();
        _memoryTelemetry?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
