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
        TaskbarContextMenu.ShowFor(this);
        e.Handled = true;
    }
}
