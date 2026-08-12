using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TaskbarMonitor;

/// <summary>
/// Embeds WPF content (a FrameworkElement) directly inside the Windows taskbar
/// using a native HwndSource child window — the reliable way to host WPF content
/// as a taskbar child (a WPF Window cannot survive SetParent; WPF resets the style).
///
/// Windows 11 25H2 taskbar layout (verified on this host):
///   Shell_TrayWnd (0→1280) contains:
///     ReBarWindow32 (442→794)  — app buttons
///     TrayNotifyWnd (1066→1280) — system tray
///   The free gap (794→1066) is where we place the widget.
/// </summary>
public sealed class TaskbarEmbedder : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private HwndSource? _source;
    private bool _attached;

    /// <summary>Gap width (px) giữa ReBar và TrayNotify — dùng làm maxWidth khi auto-resize.</summary>
    public int LastGapWidth { get; private set; } = 294;

    /// <summary>Attach the given WPF content as a child window of the taskbar.</summary>
    public bool Attach(FrameworkElement content)
    {
        if (_attached) return true;

        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;

        var rebar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (rebar == IntPtr.Zero || tray == IntPtr.Zero) return false;

        var rb = GetWindowRect(rebar);
        var tn = GetWindowRect(tray);
        int gapLeft = rb.Right;
        int gapRight = tn.Left;
        int taskbarHeight = (GetWindowRect(taskbar)).Bottom - (GetWindowRect(taskbar)).Top;
        // Dùng full gap (không cap 260) — content tự quyết width qua SizeToContent.
        // Nếu gap quá nhỏ (<80) fallback width mặc định.
        int width = Math.Max(80, gapRight - gapLeft);
        LastGapWidth = gapRight - gapLeft;
        if (width <= 0) return false;

        // The WPF root can be taller than the two-row content. Center its
        // measured visual within the real taskbar height rather than relying on
        // Explorer's child-HWND positioning.
        content.VerticalAlignment = VerticalAlignment.Center;
        content.HorizontalAlignment = HorizontalAlignment.Left;

        // Create a native child window hosted under the taskbar.
        var paramsObj = new HwndSourceParameters("TaskbarMonitorWidget")
        {
            Width = width,
            Height = taskbarHeight,
            ParentWindow = taskbar,
            WindowStyle = (int)(0x40000000 /*WS_CHILD*/ | 0x10000000 /*WS_VISIBLE*/),
            ExtendedWindowStyle = (int)(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW),
            PositionX = gapLeft - (GetWindowRect(taskbar)).Left,
            PositionY = 0,
        };
        _source = new HwndSource(paramsObj);
        _source.RootVisual = content;

        // Ensure it sits above other taskbar children.
        SetWindowPos(_source.Handle, IntPtr.Zero, gapLeft - (GetWindowRect(taskbar)).Left, 0, width, taskbarHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        _attached = true;

        // Auto-resize theo content SAU khi render xong (DesiredSize đúng lúc đó).
        content.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            AutoResize(content, taskbarHeight, gapRight - gapLeft);
        }));

        return true;
    }

    /// <summary>Đo content và resize widget cho khớp. Height giữ = taskbar (không co). Gọi lại khi layout/config đổi.</summary>
    public void AutoResize(FrameworkElement content, double maxHeight = 48, double maxWidth = 294)
    {
        if (_source == null) return;
        try
        {
            // Height luôn giữ = taskbar height (content căn giữa dọc).
            double taskbarHeight = maxHeight;

            // Ép layout đầy đủ trước khi đo.
            content.Measure(new Size(double.PositiveInfinity, taskbarHeight));
            content.Arrange(new Rect(0, 0, Math.Max(content.DesiredSize.Width, content.ActualWidth), taskbarHeight));
            content.UpdateLayout();

            double contentWidth = Math.Max(content.DesiredSize.Width, content.ActualWidth);
            if (contentWidth > 20)
            {
                Resize(Math.Min(contentWidth + 4, maxWidth), taskbarHeight);
            }
        }
        catch { /* non-critical */ }
    }

    /// <summary>Resize widget — chỉ đổi size, giữ nguyên vị trí client đã đặt lúc Attach.</summary>
    public void Resize(double width, double height)
    {
        if (_source == null) return;
        int w = Math.Max(80, (int)Math.Ceiling(width));
        int h = Math.Max(20, (int)Math.Ceiling(height));

        // Lấy vị trí client hiện tại (tương đối parent taskbar) — tránh dùng screen coords.
        GetWindowRect(_source.Handle, out var screenRect);
        POINT clientPos = new() { X = screenRect.Left, Y = screenRect.Top };
        ScreenToClient(GetParent(_source.Handle), ref clientPos);

        SetWindowPos(_source.Handle, IntPtr.Zero, clientPos.X, clientPos.Y, w, h,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    public void Dispose()
    {
        if (_source != null)
        {
            _source.RootVisual = null;
            _source.Dispose();
            _source = null;
        }
        _attached = false;
    }

    // --- P/Invoke ---
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private static RECT GetWindowRect(IntPtr hWnd)
    {
        GetWindowRect(hWnd, out var r);
        return r;
    }
}
