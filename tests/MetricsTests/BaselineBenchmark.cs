using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TaskbarMonitor.Metrics;
using Xunit;

namespace MetricsTests;

public sealed class BaselineBenchmark
{
    [Fact]
    public void RecordsStableGpuSamplingResourceBaseline()
    {
        const int sampleCycles = 250;
        using Process process = Process.GetCurrentProcess();
        using var monitor = new GpuMonitor(() => new[] { ("engine_0", 50d) });
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(50d, monitor.Sample());
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        ResourceSnapshot before = Capture(process);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < sampleCycles; i++)
        {
            Assert.Equal(50d, monitor.Sample());
        }
        stopwatch.Stop();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        ResourceSnapshot after = Capture(process);

        var result = new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SampleCycles = sampleCycles,
            DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            Before = before,
            After = after,
            HandleDelta = after.HandleCount - before.HandleCount,
            ManagedHeapDeltaBytes = after.ManagedHeapBytes - before.ManagedHeapBytes
        };
        Directory.CreateDirectory("TestResults");
        File.WriteAllText("TestResults/baseline-benchmark.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        Assert.InRange(Math.Abs(result.HandleDelta), 0, 5);
    }

    private static ResourceSnapshot Capture(Process process)
    {
        process.Refresh();
        return new ResourceSnapshot(process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false), process.HandleCount);
    }

    private sealed record ResourceSnapshot(long WorkingSetBytes, long PrivateBytes, long ManagedHeapBytes, int HandleCount);
}
