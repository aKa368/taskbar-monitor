using System;
using System.Management;

namespace TaskbarMonitor.Metrics;

/// <summary>
/// Detects whether the system is running a shared APU/SoC (CPU and iGPU on the same die)
/// or has a dedicated discrete GPU (requiring independent monitoring pods).
/// </summary>
public static class HardwareTopology
{
    private static readonly Lazy<bool> s_hasDiscreteGpu = new(DetectDiscreteGpu);

    /// <summary>Allows unit tests or manual overrides to simulate discrete GPU vs integrated APU.</summary>
    public static bool? ForceDiscreteGpu { get; set; }

    public static bool HasDiscreteGpu => ForceDiscreteGpu ?? s_hasDiscreteGpu.Value;
    public static bool IsSharedApu => !HasDiscreteGpu;

    private static bool DetectDiscreteGpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    string name = (item["Name"]?.ToString() ?? string.Empty).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    bool isDiscrete = name.Contains("geforce") || name.Contains("nvidia") || name.Contains("quadro")
                        || name.Contains("rtx") || name.Contains("gtx") || name.Contains("radeon rx")
                        || (name.Contains("arc") && name.Contains("a"));

                    bool isIntegrated = name.Contains("radeon(tm)") || name.Contains("radeon 890m")
                        || name.Contains("radeon 780m") || name.Contains("radeon 680m")
                        || name.Contains("uhd graphics") || name.Contains("iris") || name.Contains("vega");

                    if (isDiscrete && !isIntegrated)
                        return true;
                }
            }
        }
        catch { /* best-effort fallback */ }
        return false;
    }
}
