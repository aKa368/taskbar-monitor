using TaskbarMonitor.Metrics;
using Xunit;

namespace MetricsTests;

public sealed class MetricsCoreTests
{
    [Theory]
    [InlineData(-4, 0)]
    [InlineData(42, 42)]
    [InlineData(144, 100)]
    public void CpuUsageIsBounded(float input, double expected)
    {
        using var monitor = new CpuMonitor(() => input);
        Assert.Equal(expected, monitor.Sample().UsagePercent);
    }

    [Theory]
    [InlineData(1000UL, 250UL, 0.75)]
    [InlineData(0UL, 0UL, 0)]
    [InlineData(100UL, 200UL, 0)]
    public void MemoryUsageIsBounded(ulong total, ulong available, double expected) =>
        Assert.Equal(expected, MemoryMonitor.CalculateUsage(total, available));

    [Fact]
    public void NetworkCalculatesRatesAndBoundedUsage()
    {
        var start = DateTimeOffset.UnixEpoch;
        var result = NetworkMonitor.Calculate(new(100, 200, 8_000, start), new(1_100, 2_200, 8_000, start.AddSeconds(2)));
        Assert.Equal(500, result.SentBytesPerSecond);
        Assert.Equal(1000, result.ReceivedBytesPerSecond);
        Assert.Equal(1, result.Usage); // raw utilization is 150%, so it is capped.
    }

    [Fact]
    public void NetworkUsesInjectedSnapshots()
    {
        var provider = new FakeNetworkProvider(new(0, 0, 80_000, DateTimeOffset.UnixEpoch), new(1000, 1000, 80_000, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        var monitor = new NetworkMonitor(provider);
        Assert.Equal(default, monitor.Sample());
        Assert.Equal(0.2, monitor.Sample().Usage, 10);
    }

    [Fact]
    public void RingBufferWrapsInChronologicalOrder()
    {
        var buffer = new MetricRingBuffer(3);
        foreach (double value in new[] { 1, 2, 3, 4 }) buffer.AddNextChartValue(value);
        Assert.Equal(new double[] { 2, 3, 4 }, buffer.Snapshot());
    }

    [Theory]
    [InlineData(293, 19.85)]
    [InlineData(2930, 19.85)]
    public void ThermalPerformanceCounterSupportsKelvinAndTenthsKelvin(double raw, double expected)
    {
        Assert.Equal(expected, TemperatureMonitor.ConvertPerformanceCounterToCelsius(raw), 2);
    }

    [Fact]
    public void AcpiTemperatureUsesTenthsKelvin()
    {
        Assert.Equal(19.85, TemperatureMonitor.ConvertAcpiToCelsius(2930), 2);
    }

    [Fact]
    public async Task SamplerStartsAndStops()
    {
        int calls = 0;
        using var sampler = new MetricSampler(new Dictionary<string, Func<double>> { ["cpu"] = () => Interlocked.Increment(ref calls) }, 20);
        sampler.Start();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        sampler.Stop();
        int stoppedAt = Volatile.Read(ref calls);
        Assert.True(stoppedAt > 0);
        Assert.False(sampler.IsRunning);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        Assert.Equal(stoppedAt, Volatile.Read(ref calls));
    }

    [Fact]
    public void SamplerRecordsUnavailableValueWhenReaderThrows()
    {
        using var sampler = new MetricSampler(new Dictionary<string, Func<double>>
        {
            ["cpu"] = () => throw new InvalidOperationException("reader failed")
        });

        sampler.SampleNow();

        Assert.True(double.IsNaN(sampler.GetHistory("cpu").Single()));
    }

    [Fact]
    public void ThrottledReaderCachesUntilItsIntervalExpires()
    {
        int calls = 0;
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var reader = new ThrottledMetricReader(() => ++calls, TimeSpan.FromSeconds(5), clock);

        Assert.Equal(1, reader.Read());
        Assert.Equal(1, reader.Read());
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, reader.Read());
        Assert.Equal(2, calls);
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class FakeNetworkProvider(params NetworkSnapshot[] snapshots) : INetworkSnapshotProvider
    {
        private int _index;
        public NetworkSnapshot GetSnapshot() => snapshots[Math.Min(_index++, snapshots.Length - 1)];
    }
}
