using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TaskbarMonitor;

/// <summary>
/// Applies the Win32 tool-window style to taskbar-host windows. WPF's
/// ShowInTaskbar=False only controls the taskbar button; WS_EX_TOOLWINDOW is
/// also required to exclude a top-level HWND from Alt+Tab.
/// </summary>
internal static class TrayOnlyWindowStyles
{
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    public static void Apply(Window window)
    {
        window.ShowInTaskbar = false;
        window.ShowActivated = false;

        if (PresentationSource.FromVisual(window) is not null)
        {
            ApplyToHandle(window);
        }
        else
        {
            window.SourceInitialized += OnSourceInitialized;
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.SourceInitialized -= OnSourceInitialized;
            ApplyToHandle(window);
        }
    }

    private static void ApplyToHandle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var currentStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var newStyle = (currentStyle & ~WsExAppWindow) | WsExToolWindow | WsExNoActivate;
        if (newStyle != currentStyle)
        {
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(newStyle));
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new IntPtr(GetWindowLong32(handle, index));

    private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(handle, index, value) : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
