using System.Runtime.InteropServices;

namespace TaskbarMonitor.Metrics;

/// <summary>Reads physical memory through the lightweight GlobalMemoryStatusEx API.</summary>
public sealed class MemoryMonitor : IDisposable
{
    public MemoryMetrics Sample()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!OperatingSystem.IsWindows() || !GlobalMemoryStatusEx(ref status))
            return new MemoryMetrics(0, 0, 0, 0, 0, 0, 0);

        ulong used = status.TotalPhysical >= status.AvailablePhysical
            ? status.TotalPhysical - status.AvailablePhysical : 0;
        double usage = Math.Clamp(status.MemoryLoad / 100.0, 0, 1);
        if (usage <= 0 && status.TotalPhysical > 0)
            usage = Math.Clamp((double)used / status.TotalPhysical, 0, 1);

        return new MemoryMetrics(status.TotalPhysical, used, usage, 0, 0, 0, 0);
    }

    public static double CalculateUsage(ulong total, ulong available) =>
        total == 0 ? 0 : Math.Clamp((double)(total >= available ? total - available : 0) / total, 0, 1);

    public void Dispose() { }

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
