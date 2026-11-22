using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

namespace TaskbarMonitor;

/// <summary>
/// Zero-Overhead Working Set Trimmer for TaskbarMonitor.
/// Combines full compacting garbage collection with Win32 EmptyWorkingSet / SetProcessWorkingSetSize
/// to reclaim unreferenced native/managed pages and keep physical RAM footprint minimal (&lt; 25MB).
/// </summary>
public sealed class WorkingSetTrimmer : IDisposable
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    private readonly Timer? _timer;
    private int _disposed;
    private int _isTrimming;

    public WorkingSetTrimmer(TimeSpan? interval = null)
    {
        var period = interval ?? TimeSpan.FromSeconds(30);
        _timer = new Timer(
            static state => ((WorkingSetTrimmer)state!).Trim(),
            this,
            dueTime: period,
            period: period);
    }

    /// <summary>
    /// Executes a low-frequency, deep working set trim.
    /// Thread-safe and non-blocking if already in progress.
    /// </summary>
    public void Trim()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (Interlocked.CompareExchange(ref _isTrimming, 1, 0) != 0)
            return;

        try
        {
            TrimWorkingSet();
        }
        finally
        {
            Interlocked.Exchange(ref _isTrimming, 0);
        }
    }

    /// <summary>
    /// Static helper to trigger an immediate compaction and memory working set trim.
    /// Can be invoked directly during application startup or diagnostic probes.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            // 1. Compact Large Object Heap & Gen2 garbage collection
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            // 2. Win32 EmptyWorkingSet & SetProcessWorkingSetSize(-1, -1) to purge unreferenced pages
            IntPtr handle = GetCurrentProcess();
            EmptyWorkingSet(handle);
            SetProcessWorkingSetSize(handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WorkingSetTrimmer] Trim exception: {ex.GetType().Name} - {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer?.Dispose();
    }
}
