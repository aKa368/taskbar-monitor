using System;
using System.Windows.Controls;
using System.Windows.Input;

namespace TaskbarMonitor.UI;

public partial class TaskbarContent : UserControl, IDisposable
{
    public TaskbarContentViewModel ViewModel { get; }

    public TaskbarContent()
    {
        InitializeComponent();
        try
        {
            ViewModel = new TaskbarContentViewModel();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskbarContentViewModel ctor failed: {ex}");
            throw;
        }
        DataContext = ViewModel;
    }

    /// <summary>Mở context menu settings bằng chuột phải (trigger chính).</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var menu = TaskbarContextMenu.Build();
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    public void Dispose()
    {
        ViewModel.Dispose();
    }
}
