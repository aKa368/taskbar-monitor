using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace TaskbarMonitor;

/// <summary>
/// Opt-in, low-frequency process diagnostics for detecting managed or native leaks.
/// It writes only numeric process/runtime counters and no user data.
/// </summary>
internal sealed class MemoryTelemetry : IDisposable
{
    private readonly Timer _timer;
    private int _disposed;

    public MemoryTelemetry(TimeSpan? interval = null)
    {
        _timer = new Timer(
            static state => ((MemoryTelemetry)state!).Sample(),
            this,
            dueTime: interval ?? TimeSpan.FromSeconds(30),
            period: interval ?? TimeSpan.FromSeconds(30));
    }

    private void Sample()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            using Process process = Process.GetCurrentProcess();
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            Trace.WriteLine(
                $"[MemoryTelemetry] pid={process.Id} " +
                $"workingSet={process.WorkingSet64 / 1048576.0:F1}MiB " +
                $"private={process.PrivateMemorySize64 / 1048576.0:F1}MiB " +
                $"managedHeap={GC.GetTotalMemory(false) / 1048576.0:F1}MiB " +
                $"gcHeap={gc.HeapSizeBytes / 1048576.0:F1}MiB " +
                $"handles={process.HandleCount} " +
                $"threads={process.Threads.Count}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MemoryTelemetry] sample failed: {ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Dispose();
    }
}
