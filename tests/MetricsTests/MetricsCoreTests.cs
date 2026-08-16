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
    public void SamplerExposesLatestValueWithoutHistoryCopy()
    {
        using var sampler = new MetricSampler(new Dictionary<string, Func<double>>
        {
            ["cpu"] = () => 42
        });

        sampler.SampleNow();

        Assert.Equal(42, sampler.GetLatest("cpu"));
    }

    [Fact]
    public async Task SamplerSkipsOverlappingSamplePasses()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var sampler = new MetricSampler(new Dictionary<string, Func<double>>
        {
            ["cpu"] = () =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                return 1;
            }
        });

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task first = Task.Run(sampler.SampleNow, cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
        Task second = Task.Run(sampler.SampleNow, cancellationToken);
        Task completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
        Assert.Same(second, completed);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.TrySetResult(true);
        await first.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
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

    [Theory]
    [InlineData("54\r\n", 54)]
    [InlineData("61, 62", 62)]
    [InlineData("not supported", double.NaN)]
    [InlineData("131", double.NaN)]
    [InlineData("-1", double.NaN)]
    public void NvidiaTemperatureParsingIsBounded(string output, double expected)
    {
        double actual = GpuTemperatureMonitor.ParseNvidiaSmiOutput(output);
        if (double.IsNaN(expected)) Assert.True(double.IsNaN(actual));
        else Assert.Equal(expected, actual);
    }

    [Fact]
    public void NvidiaTemperatureSkipsUnavailableAndUsesHottestGpu() =>
        Assert.Equal(72, GpuTemperatureMonitor.ParseNvidiaSmiOutput("N/A\n54\ninvalid\n72\n"));

    [Fact]
    public void NvidiaResolverReturnsNullWhenExecutableIsAbsent()
    {
        var resolver = new NvidiaSmiResolver(name => name == "PATH" ? @"C:\Tools" : null, _ => false);
        Assert.Null(resolver.Resolve());
    }

    [Theory]
    [InlineData("SystemRoot", @"C:\Windows\System32\nvidia-smi.exe")]
    [InlineData("ProgramFiles", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe")]
    public void NvidiaResolverFindsCommonInstallPaths(string variable, string expected)
    {
        var values = new Dictionary<string, string?> { [variable] = variable == "SystemRoot" ? @"C:\Windows" : @"C:\Program Files" };
        var resolver = new NvidiaSmiResolver(name => values.GetValueOrDefault(name), path => path == expected);
        Assert.Equal(expected, resolver.Resolve());
    }

    [Fact]
    public void NvidiaRunnerKillsHungProcess()
    {
        var fake = new FakeNvidiaProcess(waitResult: false);
        var runner = new NvidiaSmiRunner(_ => fake);
        Assert.Null(runner.Run("nvidia-smi.exe", TimeSpan.FromMilliseconds(1)));
        Assert.True(fake.Killed);
    }

    [Fact]
    public void SelectiveInitialSampleDoesNotCallHeavyReader()
    {
        int light = 0, heavy = 0;
        using var sampler = new MetricSampler(new Dictionary<string, Func<double>>
        {
            ["cpu"] = () => ++light,
            ["gpuTemperature"] = () => ++heavy
        }, 60_000);
        sampler.SampleNow(static key => key != "gpuTemperature");
        Assert.Equal(1, light);
        Assert.Equal(0, heavy);
        Assert.Empty(sampler.GetHistory("gpuTemperature"));
    }

    [Fact]
    public void GpuAggregatesClientsPerPhysicalEngineAndUsesBusiestEngine()
    {
        var samples = new (string, double)[]
        {
            ("pid_1_luid_0x1_phys_0_eng_0_engtype_3D", 20),
            ("pid_2_luid_0x1_phys_0_eng_0_engtype_3D", 15),
            ("pid_1_luid_0x1_phys_0_eng_1_engtype_Copy", 70),
        };

        Assert.Equal(70, GpuMonitor.AggregateBusiestEngine(samples));
    }

    [Fact]
    public void GpuReturnsUnavailableWhenNoFiniteSamplesExist()
    {
        Assert.True(double.IsNaN(GpuMonitor.AggregateBusiestEngine(
            new[] { ("engine", double.NaN) })));
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


    private sealed class FakeNvidiaProcess(bool waitResult) : INvidiaSmiProcess
    {
        public bool Killed { get; private set; }
        public bool Start() => true;
        public Task<string> ReadOutputAsync() => Task.FromResult("54");
        public void DrainError() { }
        public bool WaitForExit(int milliseconds) => waitResult;
        public void Kill() => Killed = true;
        public void Dispose() { }
    }

    [Fact]
    public void WddmTemperatureUsesHottestValidRawReading()
    {
        var native = new FakeWddmNative(540, 1310, 720);
        var provider = new WddmGpuTemperatureProvider(native);
        Assert.Equal(72, provider.ReadCelsius());
        provider.Dispose();
        Assert.True(native.Disposed);
    }

    [Theory]
    [InlineData(0, 16, 0)]
    [InlineData(3, 16, 3)]
    [InlineData(99, 16, 16)]
    public void WddmEnumerationCountNeverExceedsInputCapacity(uint returned, uint capacity, uint expected) =>
        Assert.Equal(expected, D3dkmtTemperatureNative.ClampEnumeratedCount(returned, capacity));

    [Fact]
    public void WddmEnumerationUsesSdkMaximumAdapterCapacity() =>
        Assert.Equal(16u, D3dkmtTemperatureNative.MaximumAdapters);

    [Fact]
    public void RamTemperatureIsTruthfullyUnavailableWithoutProvider()
    {
        using var monitor = new RamTemperatureMonitor();
        var reading = monitor.Sample();
        Assert.True(double.IsNaN(reading.Celsius));
        Assert.Contains("no standard DIMM", reading.Reason);
    }

    private sealed class FakeWddmNative(params uint[] values) : IWddmTemperatureNative
    {
        public bool Disposed { get; private set; }
        public IReadOnlyList<uint> QueryRawTemperatures() => values;
        public void Dispose() => Disposed = true;
    }
}
