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
    private TrayIconService? _trayIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        UpdateAutostartRegistry(ConfigManager.Instance.Config.Autostart);
        ConfigManager.Instance.ConfigReloaded += (_, cfg) => UpdateAutostartRegistry(cfg.Autostart);
        _trayIcon = new TrayIconService();

        _viewModel = new TaskbarContentViewModel();
        _pairLifecycle = new TaskbarPairLifecycle(() => new TaskbarWindowPair(_viewModel));
        await _pairLifecycle.StartAsync();
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
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
