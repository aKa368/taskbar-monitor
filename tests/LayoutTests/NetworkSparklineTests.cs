using TaskbarMonitor.UI;
using Xunit;

namespace TaskbarMonitor.Tests.Layout;

public sealed class NetworkSparklineTests
{
    [Fact]
    public void RenderDownload_ProducesSparkline_AndRespectsBoundedBuffer()
    {
        var s = new NetworkSparkline();

        // Push 20 samples (capacity is 16) — only last 16 retained
        for (int i = 1; i <= 20; i++)
            s.Push(i * 100.0, 0); // down: 100..2000 Kbps, up: 0

        string spark = s.RenderDownload();

        // Exactly 16 chars (buffer capacity)
        Assert.Equal(16, spark.Length);
        Assert.Equal(16, s.Count);

        // All chars are valid block glyphs
        foreach (char c in spark)
            Assert.Contains(c, "▁▂▃▄▅▆▇█");

        // Last sample (2000 Kbps) is max → should render as top block '█'
        Assert.Equal('█', spark[^1]);

        // First retained sample (500 Kbps) is 25% of max → lower block
        Assert.NotEqual('█', spark[0]);

        // Max observed values tracked
        Assert.Equal(2000.0, s.MaxDownloadKbps);
        Assert.Equal(0.0, s.MaxUploadKbps);
    }

    [Fact]
    public void Push_IgnoresNonFiniteValues()
    {
        var s = new NetworkSparkline();
        s.Push(double.NaN, 100);
        s.Push(200, double.PositiveInfinity);
        s.Push(300, double.NegativeInfinity);

        // Only finite values counted
        Assert.Equal(3, s.Count);
        Assert.Equal(300.0, s.MaxDownloadKbps);
        Assert.Equal(100.0, s.MaxUploadKbps);
    }

    [Fact]
    public void RenderDownload_EmptyBuffer_ReturnsEmpty()
    {
        var s = new NetworkSparkline();
        Assert.Equal(string.Empty, s.RenderDownload());
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void RenderDownload_AllZero_ReturnsBottomBlocks()
    {
        var s = new NetworkSparkline();
        for (int i = 0; i < 5; i++) s.Push(0, 0);

        string spark = s.RenderDownload();
        Assert.Equal(5, spark.Length);
        Assert.All(spark, c => Assert.Equal('▁', c));
    }

    [Fact]
    public void RenderPair_ReturnsBothDirections()
    {
        var s = new NetworkSparkline();
        s.Push(100, 50);
        s.Push(200, 150);

        string pair = s.RenderPair();
        string[] parts = pair.Split(' ');

        Assert.Equal(2, parts.Length);
        Assert.Equal(2, parts[0].Length); // download
        Assert.Equal(2, parts[1].Length); // upload
    }
}