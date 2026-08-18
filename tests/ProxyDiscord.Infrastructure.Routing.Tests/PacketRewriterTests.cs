using System.Net;
using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Infrastructure.Routing;

namespace ProxyDiscord.Infrastructure.Routing.Tests;

public class PacketRewriterTests
{
    [Fact]
    public void Parse_ValidTcpPacket_ExtractsAllFields()
    {
        var packet = TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443);

        var flow = PacketRewriter.Parse(packet);

        Assert.True(flow.IsValid);
        Assert.Equal(TransportProtocol.Tcp, flow.Protocol);
        Assert.Equal("192.168.1.50", flow.SrcIp);
        Assert.Equal(51000, flow.SrcPort);
        Assert.Equal("203.0.113.10", flow.DstIp);
        Assert.Equal(443, flow.DstPort);
        Assert.False(flow.TcpFin);
        Assert.False(flow.TcpRst);
    }

    [Fact]
    public void Parse_FinFlagSet_IsReported()
    {
        var packet = TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443, fin: true);

        var flow = PacketRewriter.Parse(packet);

        Assert.True(flow.TcpFin);
        Assert.False(flow.TcpRst);
    }

    [Fact]
    public void Parse_RstFlagSet_IsReported()
    {
        var packet = TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443, rst: true);

        var flow = PacketRewriter.Parse(packet);

        Assert.True(flow.TcpRst);
    }

    [Fact]
    public void Parse_TooShortBuffer_ReturnsInvalid()
    {
        var flow = PacketRewriter.Parse(new byte[10]);

        Assert.False(flow.IsValid);
    }

    [Fact]
    public void Parse_NonIPv4Version_ReturnsInvalid()
    {
        var packet = TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443);
        packet[0] = 0x65;

        var flow = PacketRewriter.Parse(packet);

        Assert.False(flow.IsValid);
    }

    [Fact]
    public void ApplySourceNat_RewritesSourceIpAndPort_LeavesDestinationUntouched()
    {
        var packet = TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443);

        PacketRewriter.ApplySourceNat(packet, IPAddress.Parse("10.8.0.5"), 34000);
        var flow = PacketRewriter.Parse(packet);

        Assert.Equal("10.8.0.5", flow.SrcIp);
        Assert.Equal(34000, flow.SrcPort);
        Assert.Equal("203.0.113.10", flow.DstIp);
        Assert.Equal(443, flow.DstPort);
    }

    [Fact]
    public void ApplySourceNat_ZeroesChecksumFields()
    {
        var packet = TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443);
        packet[10] = 0xAB;
        packet[11] = 0xCD;
        packet[36] = 0xAB;
        packet[37] = 0xCD;

        PacketRewriter.ApplySourceNat(packet, IPAddress.Parse("10.8.0.5"), 34000);

        Assert.Equal(0, packet[10]);
        Assert.Equal(0, packet[11]);
        Assert.Equal(0, packet[36]);
        Assert.Equal(0, packet[37]);
    }

    [Fact]
    public void ApplyDestinationNat_RewritesDestinationIpAndPort_LeavesSourceUntouched()
    {
        var packet = TestPacketBuilder.BuildTcpPacket("10.8.0.5", 443, "10.8.0.9", 34000);

        PacketRewriter.ApplyDestinationNat(packet, IPAddress.Parse("192.168.1.50"), 51000);
        var flow = PacketRewriter.Parse(packet);

        Assert.Equal("10.8.0.5", flow.SrcIp);
        Assert.Equal(443, flow.SrcPort);
        Assert.Equal("192.168.1.50", flow.DstIp);
        Assert.Equal(51000, flow.DstPort);
    }
}
