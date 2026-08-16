using System.Windows.Controls;
using System.Windows.Input;

namespace TaskbarMonitor.UI;

public partial class SystemTaskbarContent : UserControl
{
    public SystemTaskbarContent(TaskbarContentViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var menu = TaskbarContextMenu.Build();
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
