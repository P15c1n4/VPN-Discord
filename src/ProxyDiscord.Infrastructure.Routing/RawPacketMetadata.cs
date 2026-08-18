namespace ProxyDiscord.Infrastructure.Routing;

public readonly record struct PacketAddress(byte[] Raw, bool Outbound, uint IfIdx, bool Loopback, bool Impostor, bool IsIpv6)
{
    public const int RAW_SIZE = 80;

    public PacketAddress AsInbound() => this with { Raw = (byte[])Raw.Clone(), Outbound = false };

    public static PacketAddress ForTest(bool outbound, uint ifIdx = 1, bool loopback = false, bool isIpv6 = false) =>
        new(new byte[RAW_SIZE], outbound, ifIdx, loopback, Impostor: false, isIpv6);
}
