using TaskbarMonitor.UI;

namespace TaskbarMonitor;

internal sealed class TaskbarWindowPair : ITaskbarWindowPair
{
    private readonly MainWindow _account;
    private readonly SystemMonitorWindow _system;
    public event EventHandler? TaskbarChanged;

    internal TaskbarWindowPair(TaskbarContentViewModel viewModel)
    {
        _account = new MainWindow(viewModel);
        _system = new SystemMonitorWindow(viewModel);
        Subscribe(_account.TaskbarContentHost);
        Subscribe(_system.TaskbarContentHost);
    }

    private void Subscribe(Deskband11Lib.Wpf.TaskbarContentHost host)
    {
        host.TaskbarWindowRecreated += Forward;
        host.TaskbarWindowDisappeared += Forward;
    }
    private void Unsubscribe(Deskband11Lib.Wpf.TaskbarContentHost host)
    {
        host.TaskbarWindowRecreated -= Forward;
        host.TaskbarWindowDisappeared -= Forward;
    }
    private void Forward(object? sender, EventArgs e) => TaskbarChanged?.Invoke(this, e);
    public Task AttachAccountAsync(CancellationToken cancellationToken) => _account.PrepareTaskbarContentAsync().WaitAsync(cancellationToken);
    public Task AttachSystemAsync(CancellationToken cancellationToken) => _system.PrepareTaskbarContentAsync().WaitAsync(cancellationToken);
    public void Show()
    {
        _account.Show();
        _system.Show();
    }
    public void Close() { _account.Close(); _system.Close(); }
    public void Dispose()
    {
        Unsubscribe(_account.TaskbarContentHost);
        Unsubscribe(_system.TaskbarContentHost);
    }
}
