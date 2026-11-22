using System.Net.NetworkInformation;
using TaskbarMonitor.Metrics;
using Xunit;

namespace MetricsTests;

/// <summary>
/// Regression lock for the doubled network speed report: sing-box TUN mode exposes
/// "singbox_tun" as Up with IfType 53 and no MAC, so the old Loopback/Tunnel-only
/// filter summed the tunnel AND the physical NIC underneath it.
/// </summary>
public sealed class NetworkAdapterFilterTests
{
    [Fact]
    public void SingBoxTunAdapterIsNotMeasured()
    {
        Assert.False(NetworkAdapterFilter.IsMeasurable(
            (NetworkInterfaceType)53, OperationalStatus.Up, 100_000_000_000, "sing-tun Tunnel", 0));
    }

    [Fact]
    public void PhysicalTetheringAdapterIsMeasured()
    {
        Assert.True(NetworkAdapterFilter.IsMeasurable(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, 426_000_000,
            "Remote NDIS based Internet Sharing Device", 6));
    }

    [Fact]
    public void WirelessAdapterIsMeasured()
    {
        Assert.True(NetworkAdapterFilter.IsMeasurable(
            NetworkInterfaceType.Wireless80211, OperationalStatus.Up, 2_400_000_000,
            "Qualcomm FastConnect 7800 Wi-Fi 7 High Band Simultaneous (HBS) Network Adapter", 6));
    }

    [Theory]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    [InlineData("WireGuard Tunnel")]
    [InlineData("Tailscale Tunnel")]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("Wintun Userspace Tunnel")]
    public void VirtualAdaptersAreNotMeasured(string description)
    {
        Assert.False(NetworkAdapterFilter.IsMeasurable(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, 10_000_000_000, description, 6));
    }

    [Fact]
    public void DownOrSpeedlessAdapterIsNotMeasured()
    {
        Assert.False(NetworkAdapterFilter.IsMeasurable(
            NetworkInterfaceType.Ethernet, OperationalStatus.Down, 1_000_000_000, "Realtek Gaming 2.5GbE", 6));
        Assert.False(NetworkAdapterFilter.IsMeasurable(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, 0, "Realtek Gaming 2.5GbE", 6));
    }
}
