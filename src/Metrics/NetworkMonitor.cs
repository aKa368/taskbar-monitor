using System.Diagnostics;
using System.Net.NetworkInformation;

namespace TaskbarMonitor.Metrics;

public readonly record struct NetworkSnapshot(ulong SentBytes, ulong ReceivedBytes, ulong LinkSpeedBitsPerSecond, DateTimeOffset Timestamp);
public readonly record struct NetworkMetrics(double SentBytesPerSecond, double ReceivedBytesPerSecond, double Usage);

public interface INetworkSnapshotProvider { NetworkSnapshot GetSnapshot(); }

/// <summary>Aggregates operational, non-loopback physical network adapters.</summary>
public sealed class PhysicalNetworkSnapshotProvider : INetworkSnapshotProvider
{
    public NetworkSnapshot GetSnapshot()
    {
        ulong sent = 0, received = 0, speed = 0;
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel || adapter.Speed <= 0)
                continue;
            try
            {
                // GetIPStatistics includes the active IP stack (IPv4/IPv6).
                // GetIPv4Statistics silently misses IPv6-only Wi-Fi paths.
                var stats = adapter.GetIPStatistics();
                sent += (ulong)Math.Max(0, stats.BytesSent);
                received += (ulong)Math.Max(0, stats.BytesReceived);
                speed += (ulong)adapter.Speed;
            }
            catch (NetworkInformationException) { }
        }
        return new(sent, received, speed, DateTimeOffset.UtcNow);
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

    /// <summary>
    /// Returns one coherent network sample for callers that need both upload
    /// and download. The metric sampler asks for those two values separately;
    /// without this small cache the second call advances the snapshot and
    /// reports an almost-zero delta.
    /// </summary>
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
