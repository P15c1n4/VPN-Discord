using ProxyDiscord.Domain.Exceptions;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Domain.Tests;

public class HostEndpointTests
{
    [Theory]
    [InlineData("vpn.example.com", "vpn.example.com", 443)]
    [InlineData("1.2.3.4", "1.2.3.4", 443)]
    public void Parse_HostWithoutPort_UsesDefaultPort(string raw, string expectedHost, int defaultPort)
    {
        var endpoint = HostEndpoint.Parse(raw, defaultPort);

        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(defaultPort, endpoint.Port);
    }

    [Theory]
    [InlineData("vpn.example.com:995", "vpn.example.com", 995)]
    [InlineData("1.2.3.4:8443", "1.2.3.4", 8443)]
    public void Parse_HostWithPort_SplitsHostAndPort(string raw, string expectedHost, int expectedPort)
    {
        var endpoint = HostEndpoint.Parse(raw, 443);

        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(expectedPort, endpoint.Port);
    }

    [Fact]
    public void Parse_InvalidPort_Throws()
    {
        Assert.Throws<AddressParseException>(() => HostEndpoint.Parse("vpn.example.com:notaport", 443));
    }

    [Fact]
    public void Parse_PortOutOfRange_TreatsWholeStringAsHost()
    {
        var ex = Record.Exception(() => HostEndpoint.Parse("vpn.example.com:99999", 443));

        Assert.IsType<AddressParseException>(ex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrWhitespace_Throws(string? raw)
    {
        Assert.Throws<AddressParseException>(() => HostEndpoint.Parse(raw, 443));
    }

    [Fact]
    public void ToString_FormatsHostColonPort()
    {
        var endpoint = HostEndpoint.Parse("vpn.example.com:995", 443);

        Assert.Equal("vpn.example.com:995", endpoint.ToString());
    }
}
