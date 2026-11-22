using WpfApplication = System.Windows.Application;
using FormsMouseButtons = System.Windows.Forms.MouseButtons;
using FormsMouseEventArgs = System.Windows.Forms.MouseEventArgs;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using TaskbarMonitor.UI;

namespace TaskbarMonitor;

/// <summary>
/// Notification-area entry point. Both tray clicks and taskbar right-clicks
/// open the same WPF menu so labels, fonts, check states, and actions stay in sync.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly FormsNotifyIcon _icon;
    private bool _disposed;

    public TrayIconService()
    {
        _icon = new FormsNotifyIcon
        {
            Icon = DrawingSystemIcons.Application,
            Text = "TaskbarMonitor",
            Visible = true
        };

        _icon.MouseUp += OnMouseUp;
    }

    private static void OnMouseUp(object? sender, FormsMouseEventArgs args)
    {
        if (args.Button != FormsMouseButtons.Left && args.Button != FormsMouseButtons.Right)
            return;

        WpfApplication.Current?.Dispatcher.BeginInvoke(TaskbarContextMenu.ShowAtCursor);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.MouseUp -= OnMouseUp;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
