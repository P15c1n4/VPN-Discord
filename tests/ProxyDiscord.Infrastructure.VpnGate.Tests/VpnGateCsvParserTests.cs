using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Infrastructure.VpnGate;

namespace ProxyDiscord.Infrastructure.VpnGate.Tests;

public class VpnGateCsvParserTests
{
    private static VpnGateCsvParser CreateParser() => new(NullLogger<VpnGateCsvParser>.Instance);

    [Fact]
    public void Parse_SkipsCommentAndHeaderAndFooterLines()
    {
        var csv = "*vpn_servers\r\n" +
                  "#HostName,IP,Score,Ping,Speed,CountryLong,CountryShort,NumVpnSessions,Uptime,TotalUsers,TotalTraffic,LogType,Operator,Message,OpenVPN_ConfigData_Base64\r\n" +
                  "public-vpn-01,1.2.3.4,1000,10,1000000,Japan,JP,5,123456,1000,999999,0,operator,,QUJD\r\n" +
                  "*\r\n";

        var rows = CreateParser().Parse(csv);

        Assert.Single(rows);
        Assert.Equal("public-vpn-01", rows[0].HostName);
        Assert.Equal("1.2.3.4", rows[0].Ip);
        Assert.Equal("QUJD", rows[0].OpenVpnConfigBase64);
    }

    [Fact]
    public void Parse_SkipsRowsWithTooFewColumns()
    {
        var csv = "public-vpn-01,1.2.3.4,1000,10\r\n" +
                  "public-vpn-02,5.6.7.8,900,20,2000000,Brazil,BR,3,222,50,4444,0,op,,WFla\r\n";

        var rows = CreateParser().Parse(csv);

        Assert.Single(rows);
        Assert.Equal("public-vpn-02", rows[0].HostName);
    }

    [Fact]
    public void Parse_MessageContainingCommas_DoesNotCorruptOpenVpnConfigColumn()
    {
        var csv = "public-vpn-03,9.9.9.9,500,30,100,Germany,DE,1,10,5,20,0,op,hello, world, ok,QmFzZTY0\r\n";

        var rows = CreateParser().Parse(csv);

        Assert.Single(rows);
        Assert.Equal("hello, world, ok", rows[0].Message);
        Assert.Equal("QmFzZTY0", rows[0].OpenVpnConfigBase64);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyList()
    {
        var rows = CreateParser().Parse("");

        Assert.Empty(rows);
    }
}
