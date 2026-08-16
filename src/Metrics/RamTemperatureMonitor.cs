namespace TaskbarMonitor.Metrics;

public readonly record struct RamTemperatureReading(double Celsius, string Reason)
{
    public bool IsAvailable => double.IsFinite(Celsius);
}

public interface IRamTemperatureProvider : IDisposable { RamTemperatureReading Read(); }

public sealed class RamTemperatureMonitor : IDisposable
{
    private readonly IRamTemperatureProvider _provider;
    public RamTemperatureMonitor() : this(new UnsupportedRamTemperatureProvider()) { }
    public RamTemperatureMonitor(IRamTemperatureProvider provider) => _provider = provider;
    public RamTemperatureReading Sample()
    {
        try { return _provider.Read(); }
        catch { return new(double.NaN, "DIMM temperature provider failed"); }
    }
    public void Dispose() => _provider.Dispose();
}

internal sealed class UnsupportedRamTemperatureProvider : IRamTemperatureProvider
{
    public RamTemperatureReading Read() => new(double.NaN, "Windows exposes no standard DIMM temperature API; no hardware driver is installed");
    public void Dispose() { }
}
