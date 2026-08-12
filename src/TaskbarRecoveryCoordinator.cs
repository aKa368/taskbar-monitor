using System;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarMonitor;

/// <summary>
/// Serializes taskbar reattachment while guaranteeing a failed attempt never
/// permanently suppresses the next Explorer-restart recovery notification.
/// </summary>
public sealed class TaskbarRecoveryCoordinator
{
    private int _running;

    /// <returns>False when another recovery is already in flight.</returns>
    public async Task<bool> RunAsync(Func<Task> recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            await recovery().ConfigureAwait(true);
            return true;
        }
        catch
        {
            // Explorer can notify before it has recreated all taskbar children.
            // Keep the tray process alive; a later notification can retry.
            return false;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}