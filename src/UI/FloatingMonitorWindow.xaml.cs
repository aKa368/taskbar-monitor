using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarMonitor.Config;

namespace TaskbarMonitor.UI;

public partial class FloatingMonitorWindow : Window
{
    private readonly TaskbarContentViewModel _viewModel;
    private readonly DispatcherTimer _fullscreenTimer;
    private bool _isLocked;
    private WinEventDelegate? _winEventDelegate;
    private IntPtr _winEventHook = IntPtr.Zero;

    public FloatingMonitorWindow(TaskbarContentViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        TrayOnlyWindowStyles.Apply(this);

        var config = ConfigManager.Instance.Config;
        _isLocked = config.FloatingLocked;
        Opacity = config.FloatingOpacity;
        _viewModel.IsFloatingVertical = config.FloatingVertical;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseDown += OnMouseDown;
        ContextMenuOpening += (_, _) => ContextMenu = TaskbarContextMenu.Build(this);
        ContextMenu = TaskbarContextMenu.Build(this);

        ConfigManager.Instance.ConfigReloaded += OnConfigReloaded;

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _fullscreenTimer.Tick += OnFullscreenWatchdogTick;
        _fullscreenTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);

        _winEventDelegate = OnWinEvent;
        _winEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        UpdateFullscreenState();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MAXIMIZE = 0xF030;
        if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_MAXIMIZE)
        {
            handled = true; // explicitly deny maximize command
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var config = ConfigManager.Instance.Config;
        if (config.FloatingX.HasValue && config.FloatingY.HasValue)
        {
            Left = config.FloatingX.Value;
            Top = config.FloatingY.Value;
        }
        else
        {
            Left = Math.Max(20, (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2);
            Top = 16;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked || e.ButtonState != MouseButtonState.Pressed)
            return;

        if (e.ClickCount == 2)
        {
            var cfg = ConfigManager.Instance.Config;
            cfg.FloatingVertical = !cfg.FloatingVertical;
            _viewModel.IsFloatingVertical = cfg.FloatingVertical;
            ConfigManager.Instance.Save();
            UpdateLayout();
            SnapToEdges();
            return;
        }

        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;

        try
        {
            DragMove();
        }
        catch { /* ignore */ }

        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;

        SnapToEdges();

        var config = ConfigManager.Instance.Config;
        config.FloatingX = Left;
        config.FloatingY = Top;
        ConfigManager.Instance.Save();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            LaunchTaskManager();
        }
    }

    public static void LaunchTaskManager()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private void SnapToEdges()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var screen = System.Windows.Forms.Screen.FromHandle(handle);
            var wa = screen.WorkingArea;
            var bounds = screen.Bounds;

            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            double marginH = CapsuleBorder.Margin.Left;
            double marginV = CapsuleBorder.Margin.Top;

            // WorkingArea snap points (flush to edge)
            double waMinLeft = wa.Left - marginH;
            double waMaxLeft = wa.Right - ActualWidth + marginH;
            double waMinTop = wa.Top - marginV;
            double waMaxTop = wa.Bottom - ActualHeight + marginV;

            // Bounds snap points (for floating over taskbar)
            double boundsMinLeft = bounds.Left - marginH;
            double boundsMaxLeft = bounds.Right - ActualWidth + marginH;
            double boundsMinTop = bounds.Top - marginV;
            double boundsMaxTop = bounds.Bottom - ActualHeight + marginV;

            // Edge magnetic snapping within 16px of WorkArea or Screen Bounds
            const double threshold = 16.0;
            if (Math.Abs(Left - waMinLeft) < threshold) Left = waMinLeft;
            else if (Math.Abs(Left - waMaxLeft) < threshold) Left = waMaxLeft;
            else if (Math.Abs(Left - boundsMinLeft) < threshold) Left = boundsMinLeft;
            else if (Math.Abs(Left - boundsMaxLeft) < threshold) Left = boundsMaxLeft;

            if (Math.Abs(Top - waMinTop) < threshold) Top = waMinTop;
            else if (Math.Abs(Top - waMaxTop) < threshold) Top = waMaxTop;
            else if (Math.Abs(Top - boundsMinTop) < threshold) Top = boundsMinTop;
            else if (Math.Abs(Top - boundsMaxTop) < threshold) Top = boundsMaxTop;

            // Clamping within physical screen bounds
            if (Left < boundsMinLeft) Left = boundsMinLeft;
            else if (Left > boundsMaxLeft) Left = boundsMaxLeft;

            if (Top < boundsMinTop) Top = boundsMinTop;
            else if (Top > boundsMaxTop) Top = boundsMaxTop;

            // Ensure window stays above taskbar (HWND_TOPMOST)
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { /* non-critical */ }
    }

    public void SetLocked(bool isLocked)
    {
        _isLocked = isLocked;
        CapsuleBorder.Cursor = _isLocked ? Cursors.Arrow : Cursors.SizeAll;
    }

    private void OnConfigReloaded(object? sender, ConfigData config)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _isLocked = config.FloatingLocked;
            Opacity = config.FloatingOpacity;
            CapsuleBorder.Cursor = _isLocked ? Cursors.Arrow : Cursors.SizeAll;
            _viewModel.IsFloatingVertical = config.FloatingVertical;
            ContextMenu = TaskbarContextMenu.Build(this);
            if (config.FloatingX.HasValue && Math.Abs(Left - config.FloatingX.Value) > 1)
                Left = config.FloatingX.Value;
            if (config.FloatingY.HasValue && Math.Abs(Top - config.FloatingY.Value) > 1)
                Top = config.FloatingY.Value;
            UpdateLayout();
            SnapToEdges();
            UpdateFullscreenState();
        }, DispatcherPriority.Loaded);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateFullscreenState();
            }
            else
            {
                Dispatcher.BeginInvoke(UpdateFullscreenState);
            }
        }
    }

    private void OnFullscreenWatchdogTick(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && Visibility == Visibility.Visible)
        {
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        UpdateFullscreenState();
    }

    private void UpdateFullscreenState()
    {
        var cfg = ConfigManager.Instance.Config;
        int state = 0;
        SHQueryUserNotificationState(out state);
        bool isD3dFullscreen = (state == 3);
        bool isGameRunning = isD3dFullscreen || (cfg.Metrics.GameOverlay && TaskbarMonitor.Metrics.GamePerformanceMonitor.Instance.IsGameRunning);

        string overlayMode = cfg.GameOverlayMode;
        bool isMiniHud = string.Equals(overlayMode, "MiniHud", StringComparison.OrdinalIgnoreCase);
        bool isAutoHide = cfg.AutoHideOnFullscreen || string.Equals(overlayMode, "AutoHide", StringComparison.OrdinalIgnoreCase);

        if (isGameRunning)
        {
            if (isMiniHud)
            {
                _viewModel.IsMiniGameHud = true;
                if (Visibility == Visibility.Collapsed)
                {
                    Visibility = Visibility.Visible;
                }
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
            else if (isAutoHide)
            {
                _viewModel.IsMiniGameHud = false;
                if (Visibility == Visibility.Visible)
                {
                    Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                _viewModel.IsMiniGameHud = false;
                if (Visibility == Visibility.Collapsed)
                {
                    Visibility = Visibility.Visible;
                }
            }
        }
        else
        {
            _viewModel.IsMiniGameHud = false;
            if (Visibility == Visibility.Collapsed)
            {
                Visibility = Visibility.Visible;
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ConfigManager.Instance.ConfigReloaded -= OnConfigReloaded;
        _fullscreenTimer.Stop();

        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventDelegate = null;

        base.OnClosed(e);
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
}
