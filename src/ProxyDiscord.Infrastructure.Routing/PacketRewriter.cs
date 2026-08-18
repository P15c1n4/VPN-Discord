using System.Buffers.Binary;
using System.Net;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public readonly record struct ParsedFlow(
    bool IsValid,
    TransportProtocol Protocol,
    string SrcIp,
    int SrcPort,
    string DstIp,
    int DstPort,
    bool TcpFin,
    bool TcpRst)
{
    public static readonly ParsedFlow INVALID = new(false, default, "", 0, "", 0, false, false);
}

internal static class PacketRewriter
{
    private const byte IPV4_VERSION = 4;

    public static ParsedFlow Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 20 || (packet[0] >> 4) != IPV4_VERSION)
        {
            return ParsedFlow.INVALID;
        }

        var ihl = (packet[0] & 0x0F) * 4;
        if (ihl < 20 || packet.Length < ihl + 4)
        {
            return ParsedFlow.INVALID;
        }

        var protocolByte = packet[9];
        if (protocolByte != (byte)TransportProtocol.Tcp && protocolByte != (byte)TransportProtocol.Udp)
        {
            return ParsedFlow.INVALID;
        }

        var protocol = (TransportProtocol)protocolByte;
        var srcIp = new IPAddress(packet.Slice(12, 4)).ToString();
        var dstIp = new IPAddress(packet.Slice(16, 4)).ToString();
        var transport = packet[ihl..];

        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(transport[..2]);
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(transport.Slice(2, 2));

        var fin = false;
        var rst = false;
        if (protocol == TransportProtocol.Tcp && transport.Length >= 14)
        {
            fin = (transport[13] & 0x01) != 0;
            rst = (transport[13] & 0x04) != 0;
        }

        return new ParsedFlow(true, protocol, srcIp, srcPort, dstIp, dstPort, fin, rst);
    }

    public static void ApplySourceNat(Span<byte> packet, IPAddress newSrcIp, int newSrcPort)
    {
        var ihl = (packet[0] & 0x0F) * 4;
        newSrcIp.GetAddressBytes().CopyTo(packet.Slice(12, 4));
        BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(ihl, 2), (ushort)newSrcPort);
        ZeroChecksums(packet, ihl);
    }

    public static void ApplyDestinationNat(Span<byte> packet, IPAddress newDstIp, int newDstPort)
    {
        var ihl = (packet[0] & 0x0F) * 4;
        newDstIp.GetAddressBytes().CopyTo(packet.Slice(16, 4));
        BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(ihl + 2, 2), (ushort)newDstPort);
        ZeroChecksums(packet, ihl);
    }

    private static void ZeroChecksums(Span<byte> packet, int ihl)
    {
        packet[10] = 0;
        packet[11] = 0;

        var protocol = packet[9];
        var transport = packet[ihl..];

        if (protocol == (byte)TransportProtocol.Tcp && transport.Length >= 20)
        {
            transport[16] = 0;
            transport[17] = 0;
        }
        else if (protocol == (byte)TransportProtocol.Udp && transport.Length >= 8)
        {
            transport[6] = 0;
            transport[7] = 0;
        }
    }
}
