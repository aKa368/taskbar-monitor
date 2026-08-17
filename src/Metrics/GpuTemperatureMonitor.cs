using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskbarMonitor.Metrics;

public interface IGpuTemperatureProvider : IDisposable { double ReadCelsius(); }

internal interface INvidiaSmiResolver { string? Resolve(); }
internal interface INvidiaSmiRunner { string? Run(string executable, TimeSpan timeout); }

public sealed class GpuTemperatureMonitor : IDisposable
{
    private readonly IGpuTemperatureProvider _provider;
    public GpuTemperatureMonitor() : this(new FallbackGpuTemperatureProvider(
        new WddmGpuTemperatureProvider(), new NvidiaSmiTemperatureProvider()))
    { }
    public GpuTemperatureMonitor(IGpuTemperatureProvider provider) => _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    public string SourceDescription => (_provider as IGpuTemperatureSourceInfo)?.SourceDescription ?? "GPU temperature provider";
    public double SampleCelsius()
    {
        try { return Validate(_provider.ReadCelsius()); }
        catch (Exception ex) { Diagnostics.ReportReaderFailure("gpu.temperature", ex); return double.NaN; }
    }

    public static double ParseNvidiaSmiOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return double.NaN;
        double hottest = double.NaN;
        foreach (string token in output.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
            value = Validate(value);
            if (double.IsFinite(value) && (!double.IsFinite(hottest) || value > hottest)) hottest = value;
        }
        return hottest;
    }
    private static double Validate(double value) => double.IsFinite(value) && value is >= 0 and <= 130 ? value : double.NaN;
    public void Dispose() => _provider.Dispose();
}

internal interface IGpuTemperatureSourceInfo { string SourceDescription { get; } }

internal sealed class FallbackGpuTemperatureProvider(params IGpuTemperatureProvider[] providers) : IGpuTemperatureProvider, IGpuTemperatureSourceInfo
{
    public string SourceDescription { get; private set; } = "GPU driver exposes no temperature telemetry";
    public double ReadCelsius()
    {
        foreach (var provider in providers)
        {
            double value = provider.ReadCelsius();
            if (double.IsFinite(value) && value is >= 0 and <= 130)
            {
                SourceDescription = (provider as IGpuTemperatureSourceInfo)?.SourceDescription ?? "GPU temperature provider";
                return value;
            }
        }
        return double.NaN;
    }
    public void Dispose() { foreach (var provider in providers) provider.Dispose(); }
}

internal interface IWddmTemperatureNative : IDisposable { IReadOnlyList<uint> QueryRawTemperatures(); }

internal sealed class WddmGpuTemperatureProvider : IGpuTemperatureProvider, IGpuTemperatureSourceInfo
{
    private readonly IWddmTemperatureNative _native;
    internal WddmGpuTemperatureProvider(IWddmTemperatureNative? native = null) => _native = native ?? new D3dkmtTemperatureNative();
    public string SourceDescription => "Windows WDDM adapter performance telemetry";
    public double ReadCelsius()
    {
        double hottest = double.NaN;
        foreach (uint raw in _native.QueryRawTemperatures())
        {
            double value = raw / 10d;
            if (value is >= 0 and <= 130 && (!double.IsFinite(hottest) || value > hottest)) hottest = value;
        }
        return hottest;
    }
    public void Dispose() => _native.Dispose();
}

internal sealed class D3dkmtTemperatureNative : IWddmTemperatureNative
{
    internal const uint MaximumAdapters = 16;
    private const int AdapterPerfData = 62;
    private const int PhysicalAdapterCount = 30;
    [StructLayout(LayoutKind.Sequential)] private struct EnumAdapters2 { public uint NumAdapters; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct AdapterInfo { public uint Handle; public Luid Luid; public uint NumSources; public int PreciseRegions; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryInfo { public uint Handle; public int Type; public IntPtr Data; public uint DataSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PerfData
    {
        public uint PhysicalAdapterIndex; public ulong MemoryFrequency; public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOc; public ulong MemoryBandwidth; public ulong PcieBandwidth;
        public uint FanRpm; public uint Power; public uint Temperature; public byte PowerStateOverride;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Handle; }
    [StructLayout(LayoutKind.Sequential)] private struct PhysicalCount { public uint Count; }
    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryInfo data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter data);

    public IReadOnlyList<uint> QueryRawTemperatures()
    {
        int size = Marshal.SizeOf<AdapterInfo>();
        IntPtr buffer = Marshal.AllocHGlobal(checked(size * (int)MaximumAdapters));
        var handles = new List<uint>();
        var values = new List<uint>();
        try
        {
            var request = new EnumAdapters2 { NumAdapters = MaximumAdapters, Adapters = buffer };
            if (D3DKMTEnumAdapters2(ref request) < 0) return values;
            uint enumerated = ClampEnumeratedCount(request.NumAdapters, MaximumAdapters);
            for (int i = 0; i < enumerated; i++)
            {
                var adapter = Marshal.PtrToStructure<AdapterInfo>(buffer + i * size);
                if (adapter.Handle == 0) continue;
                handles.Add(adapter.Handle);
                uint physicalCount = QueryPhysicalCount(adapter.Handle);
                for (uint physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
                {
                    IntPtr perfBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<PerfData>());
                    try
                    {
                        Marshal.StructureToPtr(new PerfData { PhysicalAdapterIndex = physicalIndex }, perfBuffer, false);
                        var query = new QueryInfo { Handle = adapter.Handle, Type = AdapterPerfData, Data = perfBuffer, DataSize = (uint)Marshal.SizeOf<PerfData>() };
                        if (D3DKMTQueryAdapterInfo(ref query) >= 0)
                        {
                            uint raw = Marshal.PtrToStructure<PerfData>(perfBuffer).Temperature;
                            if (raw > 0) values.Add(raw);
                        }
                    }
                    finally { Marshal.FreeHGlobal(perfBuffer); }
                }
            }
        }
        catch { }
        finally
        {
            foreach (uint handle in handles) { var close = new CloseAdapter { Handle = handle }; _ = D3DKMTCloseAdapter(ref close); }
            Marshal.FreeHGlobal(buffer);
        }
        return values;
    }
    internal static uint ClampEnumeratedCount(uint returnedCount, uint capacity) => Math.Min(returnedCount, capacity);
    private static uint QueryPhysicalCount(uint handle)
    {
        IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<PhysicalCount>());
        try
        {
            Marshal.StructureToPtr(new PhysicalCount { Count = 1 }, data, false);
            var query = new QueryInfo { Handle = handle, Type = PhysicalAdapterCount, Data = data, DataSize = (uint)Marshal.SizeOf<PhysicalCount>() };
            return D3DKMTQueryAdapterInfo(ref query) >= 0
                ? Math.Clamp(Marshal.PtrToStructure<PhysicalCount>(data).Count, 1u, 16u) : 1u;
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public void Dispose() { }
}

internal sealed class NvidiaSmiTemperatureProvider : IGpuTemperatureProvider, IGpuTemperatureSourceInfo
{
    private readonly string? _executable;
    private readonly INvidiaSmiRunner _runner;
    public string SourceDescription => "NVIDIA nvidia-smi";
    internal NvidiaSmiTemperatureProvider(INvidiaSmiResolver? resolver = null, INvidiaSmiRunner? runner = null)
    {
        _executable = (resolver ?? new NvidiaSmiResolver()).Resolve();
        _runner = runner ?? new NvidiaSmiRunner();
    }
    public double ReadCelsius() => _executable is null ? double.NaN :
        GpuTemperatureMonitor.ParseNvidiaSmiOutput(_runner.Run(_executable, TimeSpan.FromMilliseconds(1500)));
    public void Dispose() { }
}

internal sealed class NvidiaSmiResolver : INvidiaSmiResolver
{
    private readonly Func<string, string?> _getEnvironment;
    private readonly Func<string, bool> _exists;
    internal NvidiaSmiResolver(Func<string, string?>? getEnvironment = null, Func<string, bool>? exists = null)
    {
        _getEnvironment = getEnvironment ?? Environment.GetEnvironmentVariable;
        _exists = exists ?? File.Exists;
    }
    public string? Resolve()
    {
        var candidates = new List<string>();
        string? path = _getEnvironment("PATH");
        if (!string.IsNullOrWhiteSpace(path))
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.Combine(p.Trim().Trim('"'), "nvidia-smi.exe")));
        AddKnown(candidates, _getEnvironment("SystemRoot"), "System32", "nvidia-smi.exe");
        AddKnown(candidates, _getEnvironment("ProgramFiles"), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        return candidates.FirstOrDefault(_exists);
    }
    private static void AddKnown(List<string> candidates, string? root, params string[] parts)
    {
        if (!string.IsNullOrWhiteSpace(root)) candidates.Add(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }
}

internal interface INvidiaSmiProcess : IDisposable
{
    bool Start(); Task<string> ReadOutputAsync(); void DrainError(); bool WaitForExit(int milliseconds); void Kill();
}

internal sealed class NvidiaSmiRunner : INvidiaSmiRunner
{
    private readonly Func<string, INvidiaSmiProcess> _factory;
    internal NvidiaSmiRunner(Func<string, INvidiaSmiProcess>? factory = null) => _factory = factory ?? (path => new NvidiaSmiProcess(path));
    public string? Run(string executable, TimeSpan timeout)
    {
        using var process = _factory(executable);
        try
        {
            if (!process.Start()) return null;
            Task<string> output = process.ReadOutputAsync();
            process.DrainError();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds)) { try { process.Kill(); } catch { } return null; }
            return output.GetAwaiter().GetResult();
        }
        catch { return null; }
    }
}

internal sealed class NvidiaSmiProcess : INvidiaSmiProcess
{
    private readonly Process _process;
    internal NvidiaSmiProcess(string executable) => _process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }
    };
    public bool Start() => _process.Start();
    public Task<string> ReadOutputAsync() => _process.StandardOutput.ReadToEndAsync();
    public void DrainError() => _ = _process.StandardError.ReadToEndAsync();
    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
    public void Kill() => _process.Kill(entireProcessTree: true);
    public void Dispose() => _process.Dispose();
}
