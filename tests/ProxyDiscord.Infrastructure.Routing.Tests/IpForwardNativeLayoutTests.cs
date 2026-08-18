using System.Net;
using System.Runtime.InteropServices;

namespace ProxyDiscord.Infrastructure.Routing.Tests;

public class IpForwardNativeLayoutTests
{
    [Fact]
    public void MibIpForwardRow2_MatchesTheNativeLayout()
    {
        Assert.Equal(IpForwardNative.EXPECTED_ROW_SIZE, Marshal.SizeOf<MibIpForwardRow2>());
        Assert.Equal(12, Marshal.OffsetOf<MibIpForwardRow2>(nameof(MibIpForwardRow2.DestinationPrefix)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<MibIpForwardRow2>(nameof(MibIpForwardRow2.NextHop)).ToInt32());
        Assert.Equal(84, Marshal.OffsetOf<MibIpForwardRow2>(nameof(MibIpForwardRow2.Metric)).ToInt32());
    }

    [Fact]
    public void SockaddrInet_IsTwentyEightBytes_WithTheIpv4AddressAtOffsetFour()
    {
        Assert.Equal(IpForwardNative.EXPECTED_SOCKADDR_SIZE, Marshal.SizeOf<SockaddrInet>());
        Assert.Equal(4, Marshal.OffsetOf<SockaddrInet>(nameof(SockaddrInet.Ipv4)).ToInt32());
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.15.1")]
    [InlineData("255.255.255.255")]
    public void SockaddrInet_RoundTripsAnIpv4Address(string address)
    {
        var sockaddr = SockaddrInet.FromIpv4(IPAddress.Parse(address));

        Assert.Equal(IpForwardNative.AF_INET, sockaddr.Family);
        Assert.Equal(address, sockaddr.ToIpv4().ToString());
    }

    [Fact]
    public void ReadIpv4Table_ReturnsPlausibleRows()
    {
        var rows = IpForwardNative.ReadIpv4Table();

        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.Equal(IpForwardNative.AF_INET, row.DestinationPrefix.Prefix.Family);
            Assert.InRange(row.DestinationPrefix.PrefixLength, 0, 32);
            Assert.True(row.InterfaceIndex > 0);
        }

        Assert.Contains(rows, row => row.DestinationPrefix.Prefix.ToIpv4().ToString().StartsWith("127."));
    }
}
