using System.Text.RegularExpressions;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Vpn;

public readonly record struct OpenVpnRemote(string Host, int Port, TransportProtocol Transport);

public static partial class OpenVpnRemoteParser
{
    private const int DEFAULT_OPENVPN_PORT = 1194;

    [GeneratedRegex(
        @"^\s*remote\s+(?<host>\S+)(?:\s+(?<port>\d{1,5}))?(?:\s+(?<remoteProto>tcp(?:4|6)?(?:-client)?|udp(?:4|6)?))?\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RemoteDirective();

    [GeneratedRegex(
        @"^\s*proto\s+(?<proto>tcp(?:4|6)?(?:-client)?|udp(?:4|6)?)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ProtoDirective();

    public static OpenVpnRemote? TryParse(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return null;
        }

        var remote = RemoteDirective().Match(config);
        if (!remote.Success)
        {
            return null;
        }

        var port = DEFAULT_OPENVPN_PORT;
        if (remote.Groups["port"].Success &&
            (!int.TryParse(remote.Groups["port"].Value, out port) || port is <= 0 or > 65535))
        {
            return null;
        }

        var remoteProto = remote.Groups["remoteProto"];
        var profileProto = ProtoDirective().Match(config).Groups["proto"];
        var protocol = remoteProto.Success ? remoteProto.Value : profileProto.Value;
        var transport = protocol.StartsWith("udp", StringComparison.OrdinalIgnoreCase)
            ? TransportProtocol.Udp
            : TransportProtocol.Tcp;

        return new OpenVpnRemote(remote.Groups["host"].Value, port, transport);
    }
}
