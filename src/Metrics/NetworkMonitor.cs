using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TaskbarMonitor.Metrics;

public readonly record struct NetworkSnapshot(ulong SentBytes, ulong ReceivedBytes, ulong LinkSpeedBitsPerSecond, DateTimeOffset Timestamp);
public readonly record struct NetworkMetrics(double SentBytesPerSecond, double ReceivedBytesPerSecond, double Usage);

public interface INetworkSnapshotProvider { NetworkSnapshot GetSnapshot(); }

/// <summary>
/// Aggregates operational, non-loopback physical adapters. Adapter topology is
/// refreshed periodically; byte counters are read from the cached interfaces.
/// </summary>
public sealed class PhysicalNetworkSnapshotProvider : INetworkSnapshotProvider
{
    private static readonly long TopologyRefreshTicks = 30 * Stopwatch.Frequency;
    private NetworkInterface[] _adapters = [];
    private long _nextTopologyRefresh;

    public NetworkSnapshot GetSnapshot()
    {
        RefreshTopologyIfDue();
        ulong sent = 0, received = 0, speed = 0;
        foreach (NetworkInterface adapter in _adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                adapter.Speed <= 0)
                continue;

            try
            {
                var stats = adapter.GetIPStatistics();
                sent += (ulong)Math.Max(0, stats.BytesSent);
                received += (ulong)Math.Max(0, stats.BytesReceived);
                speed += (ulong)adapter.Speed;
            }
            catch (NetworkInformationException) { }
        }

        return new(sent, received, speed, DateTimeOffset.UtcNow);
    }

    private void RefreshTopologyIfDue()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < Volatile.Read(ref _nextTopologyRefresh)) return;

        try
        {
            _adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            // Keep the last known topology and retry sooner after a transient failure.
            Volatile.Write(ref _nextTopologyRefresh, now + Stopwatch.Frequency * 5);
            return;
        }

        Volatile.Write(ref _nextTopologyRefresh, now + TopologyRefreshTicks);
    }
}

public sealed class NetworkMonitor
{
    private readonly INetworkSnapshotProvider _provider;
    private NetworkSnapshot? _previous;
    private NetworkMetrics _cachedMetrics;
    private long _cachedAtTicks;

    public NetworkMonitor(INetworkSnapshotProvider? provider = null) => _provider = provider ?? new PhysicalNetworkSnapshotProvider();

    public NetworkMetrics Sample()
    {
        NetworkSnapshot current;
        try { current = _provider.GetSnapshot(); } catch { return default; }
        if (_previous is not { } previous) { _previous = current; return default; }
        _previous = current;
        return Calculate(previous, current);
    }

    public NetworkMetrics SampleCached(TimeSpan? window = null)
    {
        long now = Stopwatch.GetTimestamp();
        long maxAge = (long)((window ?? TimeSpan.FromMilliseconds(50)).TotalSeconds * Stopwatch.Frequency);
        if (_cachedAtTicks != 0 && now - _cachedAtTicks <= maxAge)
            return _cachedMetrics;

        _cachedMetrics = Sample();
        _cachedAtTicks = now;
        return _cachedMetrics;
    }

    public static NetworkMetrics Calculate(NetworkSnapshot previous, NetworkSnapshot current)
    {
        double seconds = (current.Timestamp - previous.Timestamp).TotalSeconds;
        if (seconds <= 0 || !double.IsFinite(seconds)) return default;
        ulong sentDelta = current.SentBytes >= previous.SentBytes ? current.SentBytes - previous.SentBytes : 0;
        ulong receivedDelta = current.ReceivedBytes >= previous.ReceivedBytes ? current.ReceivedBytes - previous.ReceivedBytes : 0;
        double sentRate = sentDelta / seconds;
        double receivedRate = receivedDelta / seconds;
        double usage = current.LinkSpeedBitsPerSecond == 0 ? 0 :
            Math.Clamp(8d * (sentRate + receivedRate) / current.LinkSpeedBitsPerSecond, 0, 1);
        return new(sentRate, receivedRate, usage);
    }
}
