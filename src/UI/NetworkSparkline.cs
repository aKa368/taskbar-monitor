namespace TaskbarMonitor.UI;

/// <summary>
/// Lightweight text sparkline for network history using unicode block characters.
/// Bounded ring buffer (16 samples) with zero/NaN guards.
/// </summary>
public sealed class NetworkSparkline
{
    private const int Capacity = 16;
    private readonly double[] _downloadSamples = new double[Capacity];
    private readonly double[] _uploadSamples = new double[Capacity];
    private int _index = 0;
    private int _count = 0;

    private static readonly char[] Blocks = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

    /// <summary>
    /// Push a new download/upload sample in Kbps.
    /// Non-finite values are ignored.
    /// </summary>
    public void Push(double downKbps, double upKbps)
    {
        if (double.IsFinite(downKbps))
            _downloadSamples[_index] = downKbps;
        if (double.IsFinite(upKbps))
            _uploadSamples[_index] = upKbps;

        _index = (_index + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// Render a compact unicode sparkline string for download history.
    /// Normalized against max observed in buffer; returns empty string if no data.
    /// </summary>
    public string RenderDownload()
    {
        if (_count == 0) return string.Empty;

        double max = 0;
        for (int i = 0; i < _count; i++)
            if (_downloadSamples[i] > max) max = _downloadSamples[i];

        if (max <= 0) return new string(Blocks[0], _count);

        char[] result = new char[_count];
        for (int i = 0; i < _count; i++)
        {
            int bufIdx = (_index - _count + i + Capacity) % Capacity;
            double v = _downloadSamples[bufIdx];
            int level = v <= 0 ? 0 : (int)Math.Clamp(v / max * (Blocks.Length - 1), 0, Blocks.Length - 1);
            result[i] = Blocks[level];
        }
        return new string(result);
    }

    /// <summary>
    /// Render a compact unicode sparkline string for upload history.
    /// Normalized against max observed in buffer; returns empty string if no data.
    /// </summary>
    public string RenderUpload()
    {
        if (_count == 0) return string.Empty;

        double max = 0;
        for (int i = 0; i < _count; i++)
            if (_uploadSamples[i] > max) max = _uploadSamples[i];

        if (max <= 0) return new string(Blocks[0], _count);

        char[] result = new char[_count];
        for (int i = 0; i < _count; i++)
        {
            int bufIdx = (_index - _count + i + Capacity) % Capacity;
            double v = _uploadSamples[bufIdx];
            int level = v <= 0 ? 0 : (int)Math.Clamp(v / max * (Blocks.Length - 1), 0, Blocks.Length - 1);
            result[i] = Blocks[level];
        }
        return new string(result);
    }

    /// <summary>
    /// Render both download and upload sparklines separated by a space.
    /// </summary>
    public string RenderPair() => $"{RenderDownload()} {RenderUpload()}";

    /// <summary>
    /// Current number of samples in buffer (≤ 16).
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Maximum observed download Kbps in current buffer.
    /// </summary>
    public double MaxDownloadKbps
    {
        get
        {
            double max = 0;
            for (int i = 0; i < _count; i++)
                if (_downloadSamples[i] > max) max = _downloadSamples[i];
            return max;
        }
    }

    /// <summary>
    /// Maximum observed upload Kbps in current buffer.
    /// </summary>
    public double MaxUploadKbps
    {
        get
        {
            double max = 0;
            for (int i = 0; i < _count; i++)
                if (_uploadSamples[i] > max) max = _uploadSamples[i];
            return max;
        }
    }
}