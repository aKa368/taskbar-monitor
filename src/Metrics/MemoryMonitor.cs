using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarMonitor.Metrics;

/// <summary>Reads physical memory through GlobalMemoryStatusEx and optional memory PDH counters.</summary>
public sealed class MemoryMonitor : IDisposable
{
    private readonly PerformanceCounter? _committed = TryCounter("Committed Bytes");
    private readonly PerformanceCounter? _cache = TryCounter("Cache Bytes");
    private readonly PerformanceCounter? _paged = TryCounter("Pool Paged Bytes");
    private readonly PerformanceCounter? _nonpaged = TryCounter("Pool Nonpaged Bytes");

    public MemoryMetrics Sample()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!OperatingSystem.IsWindows() || !GlobalMemoryStatusEx(ref status))
            return new MemoryMetrics(0, 0, 0, 0, 0, 0, 0);

        ulong used = status.TotalPhysical >= status.AvailablePhysical
            ? status.TotalPhysical - status.AvailablePhysical : 0;
        // MemoryLoad is the value Windows exposes to Task Manager and is more
        // reliable than hand-rolled accounting on machines with compression,
        // shared memory, or unusual firmware reservations.
        double usage = Math.Clamp(status.MemoryLoad / 100.0, 0, 1);
        if (usage <= 0 && status.TotalPhysical > 0)
            usage = Math.Clamp((double)used / status.TotalPhysical, 0, 1);
        return new MemoryMetrics(status.TotalPhysical, used, usage, Read(_committed), Read(_cache), Read(_paged), Read(_nonpaged));
    }

    public static double CalculateUsage(ulong total, ulong available) =>
        total == 0 ? 0 : Math.Clamp((double)(total >= available ? total - available : 0) / total, 0, 1);

    public void Dispose() { _committed?.Dispose(); _cache?.Dispose(); _paged?.Dispose(); _nonpaged?.Dispose(); }
    private static PerformanceCounter? TryCounter(string name) { try { return new("Memory", name, readOnly: true); } catch { return null; } }
    private static ulong Read(PerformanceCounter? counter) { try { return (ulong)Math.Max(0, counter?.NextValue() ?? 0); } catch { return 0; } }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

public readonly record struct MemoryMetrics(ulong TotalBytes, ulong UsedBytes, double Usage,
    ulong CommittedBytes, ulong CacheBytes, ulong PoolPagedBytes, ulong PoolNonpagedBytes);
