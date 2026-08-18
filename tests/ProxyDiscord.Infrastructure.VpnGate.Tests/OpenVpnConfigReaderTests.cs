using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.VpnGate.Tests;

public class OpenVpnConfigReaderTests
{
    private static string Encode(string config) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(config));

    [Theory]
    [InlineData(992)]
    [InlineData(443)]
    [InlineData(55444)]
    [InlineData(1602)]
    public void TryRead_ReadsHostAndPortFromTheRemoteDirective(int port)
    {
        var info = OpenVpnConfigReader.TryRead(Encode($"proto tcp\nremote vpn.example.com {port}\n"));

        Assert.NotNull(info);
        Assert.Equal("vpn.example.com", info!.Value.Host);
        Assert.Equal(port, info.Value.Port);
    }

    [Theory]
    [InlineData("tcp", TransportProtocol.Tcp)]
    [InlineData("tcp-client", TransportProtocol.Tcp)]
    [InlineData("udp", TransportProtocol.Udp)]
    public void TryRead_ReadsTheTransport(string proto, TransportProtocol expected)
    {
        var info = OpenVpnConfigReader.TryRead(Encode($"proto {proto}\nremote 1.2.3.4 1195\n"));

        Assert.Equal(expected, info!.Value.Transport);
    }

    [Fact]
    public void TryRead_WithoutProtoDirective_AssumesTcp()
    {
        var info = OpenVpnConfigReader.TryRead(Encode("remote 1.2.3.4 443\n"));

        Assert.Equal(TransportProtocol.Tcp, info!.Value.Transport);
    }

    [Fact]
    public void TryRead_HandlesCrLfLineEndings()
    {
        var info = OpenVpnConfigReader.TryRead(Encode("proto udp\r\nremote 1.2.3.4 1195\r\n"));

        Assert.Equal(1195, info!.Value.Port);
        Assert.Equal(TransportProtocol.Udp, info.Value.Transport);
    }

    [Fact]
    public void TryRead_IgnoresCommentedRemoteLines()
    {
        var info = OpenVpnConfigReader.TryRead(Encode(
            "# remote 9.9.9.9 1111\n#remote 8.8.8.8 2222\nproto tcp\nremote real.example.com 992\n"));

        Assert.Equal("real.example.com", info!.Value.Host);
        Assert.Equal(992, info.Value.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-valid-base64!!")]
    public void TryRead_ReturnsNullForMissingOrUnparseableConfig(string? configBase64)
    {
        Assert.Null(OpenVpnConfigReader.TryRead(configBase64));
    }

    [Fact]
    public void TryRead_ReturnsNullWhenThereIsNoRemoteDirective()
    {
        Assert.Null(OpenVpnConfigReader.TryRead(Encode("proto tcp\ncipher AES-128-CBC\n")));
    }

    [Fact]
    public void TryRead_ReturnsNullForAnOutOfRangePort()
    {
        Assert.Null(OpenVpnConfigReader.TryRead(Encode("proto tcp\nremote 1.2.3.4 99999\n")));
    }

    [Fact]
    public void TryRead_RealFeedConfig_RecoversTheAdvertisedEndpoint()
    {
        var info = OpenVpnConfigReader.TryRead(File.ReadAllText("RealVpnGateOpenVpnConfig.b64"));

        Assert.NotNull(info);
        Assert.Equal(992, info!.Value.Port);
    }
}
