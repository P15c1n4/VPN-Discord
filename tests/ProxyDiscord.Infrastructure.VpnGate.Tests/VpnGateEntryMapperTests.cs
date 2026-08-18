using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.VpnGate.Tests;

public class VpnGateEntryMapperTests
{
    private static VpnGateEntryMapper CreateMapper() => new(NullLogger<VpnGateEntryMapper>.Instance);

    private static string Config(int port, string host = "1.2.3.4", string proto = "tcp") =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"# comment\nproto {proto}\nremote {host} {port}\ncipher AES-128-CBC\n"));

    private static VpnGateCsvRow ValidRow(
        string hostName = "public-vpn-153",
        string ip = "219.100.37.109",
        string? configBase64 = null) => new(
        HostName: hostName, Ip: ip, Score: "1000", PingMs: "10", SpeedBps: "1000000",
        CountryLong: "Japan", CountryShort: "JP", NumVpnSessions: "5", Uptime: "123456",
        TotalUsers: "1000", TotalTraffic: "999999", LogType: "0", Operator: "someone",
        Message: "", OpenVpnConfigBase64: configBase64 ?? Config(443, ip));

    private static Dictionary<string, VpnGateProtocolSupport> SstpFor(params string[] hostNames) =>
        hostNames.ToDictionary(
            host => host,
            host => new VpnGateProtocolSupport(host, $"{host}.opengw.net"),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, VpnGateProtocolSupport> NoSstp() => new(StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(443)]
    [InlineData(992)]
    [InlineData(995)]
    [InlineData(1602)]
    [InlineData(55444)]
    public void Map_TakesOpenVpnEndpointFromThePublishedProfile(int port)
    {
        var result = CreateMapper().Map([ValidRow(configBase64: Config(port, "vpn.example.com"))], NoSstp());

        Assert.Single(result);
        Assert.True(result[0].SupportsOpenVpn);
        Assert.Equal(port, result[0].OpenVpnEndpoint!.Port);
        Assert.Equal("vpn.example.com", result[0].OpenVpnEndpoint!.Host);
    }

    [Theory]
    [InlineData("tcp", TransportProtocol.Tcp)]
    [InlineData("udp", TransportProtocol.Udp)]
    public void Map_RecordsWhetherTheProfileIsTcpOrUdp(string proto, TransportProtocol expected)
    {
        var result = CreateMapper().Map([ValidRow(configBase64: Config(1195, proto: proto))], NoSstp());

        Assert.Equal(expected, result[0].OpenVpnTransport);
    }

    [Fact]
    public void Map_OffersSstpOnlyWhenTheServerAdvertisesIt()
    {
        var mapper = CreateMapper();

        var withSstp = mapper.Map([ValidRow(hostName: "public-vpn-153")], SstpFor("public-vpn-153"));
        var withoutSstp = mapper.Map([ValidRow(hostName: "public-vpn-153")], NoSstp());

        Assert.True(withSstp[0].SupportsSstp);
        Assert.Equal("public-vpn-153.opengw.net", withSstp[0].SstpEndpoint!.Host);
        Assert.Equal(443, withSstp[0].SstpEndpoint!.Port);

        Assert.False(withoutSstp[0].SupportsSstp);
        Assert.Null(withoutSstp[0].SstpEndpoint);
    }

    [Fact]
    public void Map_SstpPortIsAlways443_NeverTheOpenVpnPort()
    {
        var result = CreateMapper().Map(
            [ValidRow(hostName: "node", configBase64: Config(1195, proto: "udp"))],
            SstpFor("node"));

        Assert.Equal(1195, result[0].OpenVpnEndpoint!.Port);
        Assert.Equal(443, result[0].SstpEndpoint!.Port);
    }

    [Fact]
    public void Map_ServerWithNeitherProtocol_IsDroppedFromTheList()
    {
        var result = CreateMapper().Map(
            [ValidRow(hostName: "useless", configBase64: ""), ValidRow(hostName: "usable")],
            NoSstp());

        Assert.Single(result);
        Assert.Equal("usable", result[0].HostName);
    }

    [Fact]
    public void Map_ServerWithOnlySstp_IsKept()
    {
        var result = CreateMapper().Map(
            [ValidRow(hostName: "sstp-only", configBase64: "")],
            SstpFor("sstp-only"));

        Assert.Single(result);
        Assert.False(result[0].SupportsOpenVpn);
        Assert.True(result[0].SupportsSstp);
        Assert.Equal(VpnProtocol.Sstp, result[0].PreferredProtocol);
    }

    [Fact]
    public void Map_ServerWithBothProtocols_PrefersOpenVpn()
    {
        var result = CreateMapper().Map([ValidRow(hostName: "both")], SstpFor("both"));

        Assert.Equal(VpnProtocol.OpenVpn, result[0].PreferredProtocol);
        Assert.Equal([VpnProtocol.OpenVpn, VpnProtocol.Sstp], result[0].SupportedProtocols);
        Assert.Equal("OpenVPN (TCP), MS-SSTP", result[0].ProtocolSummary);
    }

    [Fact]
    public void Map_SstpEndpointIsTheHostName_NeverTheIp()
    {
        var result = CreateMapper().Map(
            [ValidRow(hostName: "public-vpn-153", ip: "219.100.37.109")],
            SstpFor("public-vpn-153"));

        Assert.Equal("219.100.37.109", result[0].IpAddress);
        Assert.NotEqual("219.100.37.109", result[0].SstpEndpoint!.Host);
        Assert.EndsWith(".opengw.net", result[0].SstpEndpoint!.Host);
    }

    [Fact]
    public void Map_MatchesScrapedSupport_WhenTheCsvHostNameIsAlreadyQualified()
    {
        var result = CreateMapper().Map(
            [ValidRow(hostName: "kanratown.opengw.net")],
            SstpFor("kanratown"));

        Assert.True(result[0].SupportsSstp);
    }

    [Fact]
    public void Map_RowWithoutHostName_IsSkipped()
    {
        var result = CreateMapper().Map([ValidRow(hostName: ""), ValidRow(hostName: "ok-server")], NoSstp());

        Assert.Single(result);
        Assert.Equal("ok-server", result[0].HostName);
    }

    [Fact]
    public void Map_ValidRow_ParsesRemainingNumericAndTextFields()
    {
        var entry = CreateMapper().Map([ValidRow()], NoSstp())[0];

        Assert.Equal(1000, entry.Score);
        Assert.Equal(10, entry.PingMs);
        Assert.Equal(1_000_000, entry.SpeedBps);
        Assert.Equal("Japan", entry.CountryLong);
        Assert.Equal("JP", entry.CountryShort);
    }

    [Fact]
    public void Map_UnparseableNumericField_DefaultsToZeroInsteadOfThrowing()
    {
        var result = CreateMapper().Map([ValidRow() with { Score = "not-a-number" }], NoSstp());

        Assert.Single(result);
        Assert.Equal(0, result[0].Score);
    }
}
