using System.Text.RegularExpressions;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Vpn;

public readonly record struct OpenVpnRemote(string Host, int Port, TransportProtocol Transport);

public static partial class OpenVpnRemoteParser
{
    [GeneratedRegex(@"^\s*remote\s+(?<host>\S+)\s+(?<port>\d{1,5})\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RemoteDirective();

    [GeneratedRegex(@"^\s*proto\s+(?<proto>tcp|tcp-client|udp)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ProtoDirective();

    public static OpenVpnRemote? TryParse(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return null;
        }

        var remote = RemoteDirective().Match(config);
        if (!remote.Success ||
            !int.TryParse(remote.Groups["port"].Value, out var port) ||
            port is <= 0 or > 65535)
        {
            return null;
        }

        var proto = ProtoDirective().Match(config);
        var transport = proto.Success &&
                        proto.Groups["proto"].Value.StartsWith("udp", StringComparison.OrdinalIgnoreCase)
            ? TransportProtocol.Udp
            : TransportProtocol.Tcp;

        return new OpenVpnRemote(remote.Groups["host"].Value, port, transport);
    }
}
