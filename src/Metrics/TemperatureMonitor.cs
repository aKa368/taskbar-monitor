using System.Diagnostics;
using System.Management;

namespace TaskbarMonitor.Metrics;

/// <summary>Best-effort CPU thermal-zone reader backed by Windows Performance Counters.</summary>
public sealed class TemperatureMonitor : IDisposable
{
    private readonly List<PerformanceCounter> _counters = [];

    public TemperatureMonitor()
    {
        try
        {
            var category = new PerformanceCounterCategory("Thermal Zone Information");
            foreach (string instance in category.GetInstanceNames())
            {
                try
                {
                    _counters.Add(new PerformanceCounter(category.CategoryName, "Temperature", instance, readOnly: true));
                }
                catch
                {
                    // One unavailable thermal zone must not disable the others.
                }
            }
        }
        catch
        {
            // The category is not present on every Windows machine.
        }
    }

    public double SampleCelsius()
    {
        double total = 0;
        int count = 0;
        foreach (var counter in _counters)
        {
            try
            {
                double raw = counter.NextValue();
                double celsius = ConvertPerformanceCounterToCelsius(raw);
                if (double.IsFinite(celsius) && celsius >= 0 && celsius <= 130)
                {
                    total += celsius;
                    count++;
                }
            }
            catch { }
        }

        if (count > 0) return total / count;

        // Many laptops expose temperature only through ACPI WMI. This is
        // best-effort and may legitimately return no rows on desktop PCs.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject item in searcher.Get())
            {
                if (item["CurrentTemperature"] is uint raw)
                {
                    double celsius = ConvertAcpiToCelsius(raw);
                    if (celsius is >= 0 and <= 130)
                    {
                        total += celsius;
                        count++;
                    }
                }
            }
        }
        catch { }

        return count == 0 ? 0 : total / count;
    }

    /// <summary>Thermal performance counters may expose Kelvin or tenths of Kelvin.</summary>
    public static double ConvertPerformanceCounterToCelsius(double raw)
        => raw >= 1000 ? raw / 10.0 - 273.15 : raw - 273.15;

    /// <summary>ACPI WMI CurrentTemperature is tenths of Kelvin.</summary>
    public static double ConvertAcpiToCelsius(double raw) => raw / 10.0 - 273.15;

    public static double ConvertRawToCelsius(double raw) => ConvertAcpiToCelsius(raw);

    public void Dispose()
    {
        foreach (var counter in _counters) counter.Dispose();
        _counters.Clear();
    }
}
