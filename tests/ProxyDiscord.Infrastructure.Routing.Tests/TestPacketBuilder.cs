using System.Buffers.Binary;
using System.Net;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing.Tests;

internal static class TestPacketBuilder
{
    public static byte[] BuildTcpPacket(string srcIp, int srcPort, string dstIp, int dstPort, bool fin = false, bool rst = false)
    {
        var packet = new byte[40];

        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 40);
        packet[8] = 64;
        packet[9] = (byte)TransportProtocol.Tcp;
        IPAddress.Parse(srcIp).GetAddressBytes().CopyTo(packet.AsSpan(12, 4));
        IPAddress.Parse(dstIp).GetAddressBytes().CopyTo(packet.AsSpan(16, 4));

        var tcp = packet.AsSpan(20, 20);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[..2], (ushort)srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(2, 2), (ushort)dstPort);
        tcp[12] = 0x50;
        byte flags = 0;
        if (fin) flags |= 0x01;
        if (rst) flags |= 0x04;
        tcp[13] = flags;

        return packet;
    }

    public static byte[] BuildUdpPacket(string srcIp, int srcPort, string dstIp, int dstPort)
    {
        var packet = new byte[28];

        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)packet.Length);
        packet[8] = 64;
        packet[9] = (byte)TransportProtocol.Udp;
        IPAddress.Parse(srcIp).GetAddressBytes().CopyTo(packet.AsSpan(12, 4));
        IPAddress.Parse(dstIp).GetAddressBytes().CopyTo(packet.AsSpan(16, 4));

        var udp = packet.AsSpan(20, 8);
        BinaryPrimitives.WriteUInt16BigEndian(udp[..2], (ushort)srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp.Slice(2, 2), (ushort)dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp.Slice(4, 2), 8);

        return packet;
    }

    public static byte[] BuildIpv6TcpPacket(int srcPort, int dstPort)
    {
        const int HEADER_LENGTH = 40;
        var packet = new byte[HEADER_LENGTH + 20];

        packet[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 20);
        packet[6] = (byte)TransportProtocol.Tcp;
        packet[7] = 64;

        var tcp = packet.AsSpan(HEADER_LENGTH, 20);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[..2], (ushort)srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.Slice(2, 2), (ushort)dstPort);
        tcp[12] = 0x50;

        return packet;
    }

    public static (string SrcIp, int SrcPort, string DstIp, int DstPort) ReadAddressing(byte[] packet)
    {
        var srcIp = new IPAddress(packet.AsSpan(12, 4)).ToString();
        var dstIp = new IPAddress(packet.AsSpan(16, 4)).ToString();
        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20, 2));
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(22, 2));
        return (srcIp, srcPort, dstIp, dstPort);
    }
}
