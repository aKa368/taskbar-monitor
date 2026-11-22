namespace TaskbarMonitor.UI;

/// <summary>Formats the two network directions independently for the Grid layout.</summary>
public static class NetworkRateTextFormatter
{
    public static string FormatUpload(float kbps) => Format("↑", kbps);

    public static string FormatDownload(float kbps) => Format("↓", kbps);

    public static string FormatPair(float uploadKbps, float downloadKbps) =>
        $"{FormatValue("↑", uploadKbps)} {FormatValue("↓", downloadKbps)}";

    /// <summary>
    /// Formats the pair with an appended download sparkline (upload sparkline optional).
    /// Keeps backward compatibility: existing FormatPair signature unchanged.
    /// </summary>
    public static string FormatPairWithSparkline(float uploadKbps, float downloadKbps, NetworkSparkline? sparkline = null)
    {
        string baseText = FormatPair(uploadKbps, downloadKbps);
        if (sparkline == null) return baseText;

        sparkline.Push(downloadKbps, uploadKbps);
        string spark = sparkline.RenderDownload();
        return string.IsNullOrEmpty(spark) ? baseText : $"{baseText} {spark}";
    }

    private static string Format(string direction, float kbps)
    {
        string value = FormatValue(direction, kbps);
        return float.IsFinite(kbps) ? value + "/s" : value;
    }

    private static string FormatValue(string direction, float kbps)
    {
        if (!float.IsFinite(kbps)) return $"{direction} --";

        // The fixed Grid cell needs compact unit glyphs only. Direction conveys
        // transfer semantics, so '/s' is intentionally omitted in the merged pod.
        const float kilobytesPerMegabyte = 1024f;
        const float kilobytesPerGigabyte = kilobytesPerMegabyte * 1024f;
        const float maximumDisplayGigabytes = 999f;

        if (kbps >= maximumDisplayGigabytes * kilobytesPerGigabyte)
            return $"{direction}999G+";
        if (kbps >= kilobytesPerGigabyte)
            return $"{direction}{kbps / kilobytesPerGigabyte:F1}G";
        if (kbps >= kilobytesPerMegabyte)
            return $"{direction}{kbps / kilobytesPerMegabyte:F1}M";
        return $"{direction}{kbps:F0}K";
    }
}