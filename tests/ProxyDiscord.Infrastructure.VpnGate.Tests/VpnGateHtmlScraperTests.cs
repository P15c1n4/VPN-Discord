using Microsoft.Extensions.Logging.Abstractions;

namespace ProxyDiscord.Infrastructure.VpnGate.Tests;

public class VpnGateHtmlScraperTests
{
    private static VpnGateHtmlScraper CreateScraper() => new(NullLogger<VpnGateHtmlScraper>.Instance);

    private const string ROW_WITH_SSTP = """
        <tr>
        <td class='vg_table_row_0'><b><span>public-vpn-153.opengw.net</span></b><br><span>219.100.37.109</span></td>
        <td class='vg_table_row_0'><a href='do_openvpn.aspx?fqdn=public-vpn-153.opengw.net&ip=219.100.37.109&tcp=443&udp=0'><b>OpenVPN<BR>Config file</b></a><br>TCP: 443</td>
        <td class='vg_table_row_0'><a href='howto_sstp.aspx'><b>MS-SSTP<BR>Connect guide</b></a><p><span>SSTP Hostname :<br /><b><span style='color: #006600;' >public-vpn-153.opengw.net</span></b></span></p></td>
        </tr>
        """;

    private const string ROW_WITHOUT_SSTP = """
        <tr>
        <td class='vg_table_row_1'><b><span>vpn100383739.opengw.net</span></b><br><span>128.211.249.131</span></td>
        <td class='vg_table_row_1'><a href='do_openvpn.aspx?fqdn=vpn100383739.opengw.net&ip=128.211.249.131&tcp=0&udp=1195'><b>OpenVPN<BR>Config file</b></a><br>UDP: 1195</td>
        <td class='vg_table_row_1' style='text-align: center;'></td>
        </tr>
        """;

    [Fact]
    public void Parse_FindsTheSstpHostNameWhenTheServerAdvertisesIt()
    {
        var support = CreateScraper().Parse(ROW_WITH_SSTP);

        Assert.True(support.TryGetValue("public-vpn-153", out var entry));
        Assert.Equal("public-vpn-153.opengw.net", entry.SstpHostName);
    }

    [Fact]
    public void Parse_ReportsNoSstpWhenTheCellIsEmpty()
    {
        var support = CreateScraper().Parse(ROW_WITHOUT_SSTP);

        Assert.True(support.TryGetValue("vpn100383739", out var entry));
        Assert.Null(entry.SstpHostName);
    }

    [Fact]
    public void Parse_HandlesAWholeTableAndKeepsThemApart()
    {
        var support = CreateScraper().Parse($"<table>{ROW_WITH_SSTP}{ROW_WITHOUT_SSTP}</table>");

        Assert.Equal(2, support.Count);
        Assert.NotNull(support["public-vpn-153"].SstpHostName);
        Assert.Null(support["vpn100383739"].SstpHostName);
    }

    [Fact]
    public void Parse_HostNameLookupIsCaseInsensitive()
    {
        var support = CreateScraper().Parse(ROW_WITH_SSTP);

        Assert.True(support.ContainsKey("PUBLIC-VPN-153"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>página completamente diferente</body></html>")]
    [InlineData("<tr><td>sem hostname nenhum</td></tr>")]
    public void Parse_UnrecognisedMarkup_YieldsAnEmptyResultInsteadOfThrowing(string html)
    {
        var support = CreateScraper().Parse(html);

        Assert.Empty(support);
    }
}
