using System.Net;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed record RedirectedFlow(
    Domain.ValueObjects.TransportProtocol Protocol,
    string OriginalSourceIp,
    int SourcePort,
    string OriginalDestinationIp,
    int OriginalDestinationPort);

internal static class RedirectPacketRewriter
{
    public static void RedirectToRelay(Span<byte> packet, string localIp, int relayPort) =>
        PacketRewriter.ApplyDestinationNat(packet, IPAddress.Parse(localIp), relayPort);

    public static void RestoreFromRelay(Span<byte> packet, RedirectedFlow flow) =>
        PacketRewriter.ApplySourceNat(
            packet, IPAddress.Parse(flow.OriginalDestinationIp), flow.OriginalDestinationPort);
}
