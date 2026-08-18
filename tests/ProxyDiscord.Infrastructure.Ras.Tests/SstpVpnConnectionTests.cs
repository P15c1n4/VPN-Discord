using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Ras.Tests;

public class SstpVpnConnectionTests
{
    private static VpnConnectionRequest RequestFor(string rawAddress) => new(
        HostEndpoint.Parse(rawAddress, VpnProtocol.Sstp.DefaultPort()),
        VpnProtocol.Sstp,
        "vpn",
        "vpn",
        "entry");

    [Theory]
    [InlineData("vpn465380411.opengw.net:1887", "vpn465380411.opengw.net:1887")]
    [InlineData("vpn622746048.opengw.net:995", "vpn622746048.opengw.net:995")]
    [InlineData("vpn.example.com", "vpn.example.com:443")]
    public void BuildServerAddress_IncludesThePort(string rawAddress, string expected)
    {
        var serverAddress = SstpVpnConnection.BuildServerAddress(RequestFor(rawAddress));

        Assert.Equal(expected, serverAddress);
    }
}
