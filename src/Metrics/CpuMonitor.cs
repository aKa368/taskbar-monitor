using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Reads total processor utilization from the cheap Kernel32 GetSystemTimes API.
/// The frequency counter is optional and throttled because it is not needed for
/// the utilization percentage itself.
/// </summary>
public sealed class CpuMonitor : IDisposable
{
    private readonly Func<float>? _usageReader;
    private readonly Func<float>? _frequencyReader;
    private readonly PerformanceCounter? _frequencyCounter;
    private readonly bool _useSystemTimes;
    private long _previousIdle;
    private long _previousKernel;
    private long _previousUser;
    private bool _hasPreviousTimes;
    private long _nextFrequencyRead;
    private double _lastFrequency;
    private double _lastUsage;

    public CpuMonitor()
    {
        _useSystemTimes = true;
        _frequencyCounter = TryCreate("Processor Information", "Processor Frequency", "_Total");
    }

    public CpuMonitor(Func<float> usageReader, Func<float>? frequencyReader = null)
    {
        _usageReader = usageReader ?? throw new ArgumentNullException(nameof(usageReader));
        _frequencyReader = frequencyReader ?? (() => 0);
    }

    public double LastUsagePercent => _lastUsage;

    public CpuMetrics Sample()
    {
        if (!_useSystemTimes)
            return new(ClampPercent(SafeRead(_usageReader!)), Math.Max(0, SafeRead(_frequencyReader!)));

        double usage = ReadSystemUsage();
        double frequency = ReadFrequency();
        return new(ClampPercent(usage), Math.Max(0, frequency));
    }

    public static double CalculateUsage(long idleDelta, long kernelDelta, long userDelta)
    {
        long totalDelta = kernelDelta + userDelta;
        long busyDelta = totalDelta - idleDelta;
        if (totalDelta <= 0 || busyDelta < 0) return double.NaN;
        return Math.Clamp(100d * busyDelta / totalDelta, 0, 100);
    }

    public static double ClampPercent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    public void Dispose() => _frequencyCounter?.Dispose();

    private double ReadSystemUsage()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
            return _lastUsage;

        long idleTicks = idle.ToInt64();
        long kernelTicks = kernel.ToInt64();
        long userTicks = user.ToInt64();
        if (!_hasPreviousTimes)
        {
            _previousIdle = idleTicks;
            _previousKernel = kernelTicks;
            _previousUser = userTicks;
            _hasPreviousTimes = true;
            _lastUsage = 0;
            return _lastUsage;
        }

        double usage = CalculateUsage(
            idleTicks - _previousIdle,
            kernelTicks - _previousKernel,
            userTicks - _previousUser);
        _previousIdle = idleTicks;
        _previousKernel = kernelTicks;
        _previousUser = userTicks;
        if (double.IsFinite(usage)) _lastUsage = usage;
        return _lastUsage;
    }

    private double ReadFrequency()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextFrequencyRead) return _lastFrequency;
        _nextFrequencyRead = now + 5 * Stopwatch.Frequency;
        try
        {
            double value = _frequencyCounter?.NextValue() ?? 0;
            if (double.IsFinite(value) && value >= 0) _lastFrequency = value;
        }
        catch { }
        return _lastFrequency;
    }

    private static float SafeRead(Func<float> reader) { try { return reader(); } catch { return 0; } }
    private static PerformanceCounter? TryCreate(string category, string counter, string instance)
    {
        try { return new PerformanceCounter(category, counter, instance, readOnly: true); }
        catch { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
        public long ToInt64() => ((long)HighDateTime << 32) | LowDateTime;
    }
}

public readonly record struct CpuMetrics(double UsagePercent, double FrequencyMhz);
