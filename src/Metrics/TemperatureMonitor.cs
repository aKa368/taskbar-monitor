using System.Diagnostics;
using System.Management;

namespace TaskbarMonitor.Metrics;

/// <summary>Best-effort CPU thermal-zone reader backed by Windows Performance Counters.</summary>
public sealed class TemperatureMonitor : IDisposable
{
    private readonly List<PerformanceCounter> _counters = [];
    public string SourceDescription { get; private set; } = "Windows thermal zone unavailable";

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
                catch (Exception ex)
                {
                    // One unavailable thermal zone must not disable the others.
                    Diagnostics.ReportReaderFailure("temperature.zone", ex);
                }
            }
        }
        catch (Exception ex)
        {
            // The category is not present on every Windows machine.
            Diagnostics.ReportReaderFailure("temperature.init", ex);
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
            catch (Exception ex)
            {
                Diagnostics.ReportReaderFailure("temperature.sample", ex);
            }
        }

        if (count > 0)
        {
            SourceDescription = "Windows Thermal Zone Information (ACPI zone; not guaranteed CPU package temperature)";
            return total / count;
        }

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
        catch (Exception ex)
        {
            Diagnostics.ReportReaderFailure("temperature.wmi", ex);
        }

        if (count == 0) return double.NaN;
        SourceDescription = "MSAcpi_ThermalZoneTemperature (ACPI zone; not guaranteed CPU package temperature)";
        return total / count;
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
