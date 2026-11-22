using System.Windows.Controls;
using System.Windows.Input;

namespace TaskbarMonitor.UI;

public partial class AccountTaskbarContent : UserControl
{
    public AccountTaskbarContent(TaskbarContentViewModel viewModel)
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
