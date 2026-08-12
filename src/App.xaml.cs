using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using TaskbarMonitor.Config;

namespace TaskbarMonitor;

public partial class App : Application
{
    private const string MutexName = "TaskbarMonitor_SingleInstance_Mutex_AGY";
    private static Mutex? _singleInstanceMutex;
    private MainWindow? _window;
    private TrayIconService? _trayIcon;
    private readonly TaskbarRecoveryCoordinator _taskbarRecovery = new();

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

        await InitializeMainWindowAsync();
    }

    private async Task InitializeMainWindowAsync()
    {
        var window = new MainWindow();
        _window = window;
        window.TaskbarContentHost.TaskbarWindowRecreated += OnTaskbarWindowChanged;
        window.TaskbarContentHost.TaskbarWindowDisappeared += OnTaskbarWindowChanged;

        // Attach before Show, matching Deskband11Lib's WPF sample. The HWND
        // exists, is prepared, and is parented before it can flash on screen.
        await window.PrepareTaskbarContentAsync();
        window.Show();
    }

    private async void OnTaskbarWindowChanged(object? sender, EventArgs e)
    {
        await _taskbarRecovery.RunAsync(async () =>
        {
            if (_window == null) return;

            var oldWindow = _window;
            _window = null;
            oldWindow.TaskbarContentHost.TaskbarWindowRecreated -= OnTaskbarWindowChanged;
            oldWindow.TaskbarContentHost.TaskbarWindowDisappeared -= OnTaskbarWindowChanged;

            // Explorer has just replaced the taskbar HWND. Rebuild the hosted
            // WPF window instead of trying to reuse a destroyed child HWND.
            await Task.Delay(1000);
            oldWindow.Close();
            await InitializeMainWindowAsync();
        });
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
        _window?.Close();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}