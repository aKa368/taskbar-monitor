using System.Diagnostics;
using System.Management;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Best-effort thermal-zone reader. Performance-counter zones are read first;
/// WMI is a slow fallback and is attempted only after a cooldown.
/// </summary>
public sealed class TemperatureMonitor : IDisposable
{
    private readonly List<PerformanceCounter> _counters = [];
    private readonly TimeProvider _timeProvider;
    private long _nextWmiAttempt;
    private double _lastWmiValue = double.NaN;
    private double _lastValue = double.NaN;

    public string SourceDescription { get; private set; } = "Windows thermal zone unavailable";

    public TemperatureMonitor(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        try
        {
            var category = new PerformanceCounterCategory("Thermal Zone Information");
            foreach (string instance in category.GetInstanceNames())
            {
                try { _counters.Add(new PerformanceCounter(category.CategoryName, "Temperature", instance, readOnly: true)); }
                catch (Exception ex) { Diagnostics.ReportReaderFailure("temperature.zone", ex); }
            }
        }
        catch (Exception ex) { Diagnostics.ReportReaderFailure("temperature.init", ex); }
    }

    public double SampleCelsius(double cpuUsagePercent = double.NaN)
    {
        double total = 0;
        int count = 0;
        foreach (var counter in _counters)
        {
            try
            {
                double celsius = ConvertPerformanceCounterToCelsius(counter.NextValue());
                if (celsius is >= 0 and <= 130) { total += celsius; count++; }
            }
            catch (Exception ex) { Diagnostics.ReportReaderFailure("temperature.sample", ex); }
        }

        if (count > 0)
        {
            double candidate = total / count;
            if (IsPlausibleCpuTemperature(candidate, cpuUsagePercent))
            {
                SourceDescription = "Windows Thermal Zone Information (ACPI zone; not guaranteed CPU package temperature)";
                _lastValue = candidate;
                return _lastValue;
            }

            SourceDescription = "ACPI thermal zone rejected as non-CPU temperature";
            _lastValue = double.NaN;
            return _lastValue;
        }

        long now = _timeProvider.GetTimestamp();
        if (now >= Volatile.Read(ref _nextWmiAttempt))
        {
            Volatile.Write(ref _nextWmiAttempt, now + 30 * Stopwatch.Frequency);
            _lastWmiValue = TryReadAcpiWmi();
        }

        if (double.IsFinite(_lastWmiValue))
        {
            if (IsPlausibleCpuTemperature(_lastWmiValue, cpuUsagePercent))
            {
                SourceDescription = "MSAcpi_ThermalZoneTemperature (ACPI zone; not guaranteed CPU package temperature)";
                _lastValue = _lastWmiValue;
            }
            else
            {
                SourceDescription = "ACPI thermal zone rejected as non-CPU temperature";
                _lastValue = double.NaN;
            }
        }

        return _lastValue;
    }

    private double TryReadAcpiWmi()
    {
        double total = 0;
        int count = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var items = searcher.Get();
            foreach (ManagementObject item in items)
            {
                using (item)
                {
                    if (item["CurrentTemperature"] is uint raw)
                    {
                        double celsius = ConvertAcpiToCelsius(raw);
                        if (celsius is >= 0 and <= 130) { total += celsius; count++; }
                    }
                }
            }
        }
        catch (Exception ex) { Diagnostics.ReportReaderFailure("temperature.wmi", ex); }

        return count == 0 ? double.NaN : total / count;
    }

    public static bool IsPlausibleCpuTemperature(double celsius, double cpuUsagePercent)
    {
        if (!double.IsFinite(celsius) || celsius is < 0 or > 130) return false;
        // A thermal zone near room temperature is commonly an ambient/chassis
        // zone, not the CPU package. Never present it as CPU temperature.
        return celsius >= 30;
    }

    public static double ConvertPerformanceCounterToCelsius(double raw)
        => raw >= 1000 ? raw / 10.0 - 273.15 : raw - 273.15;

    public static double ConvertAcpiToCelsius(double raw) => raw / 10.0 - 273.15;
    public static double ConvertRawToCelsius(double raw) => ConvertAcpiToCelsius(raw);

    public void Dispose()
    {
        foreach (var counter in _counters) counter.Dispose();
        _counters.Clear();
    }
}
