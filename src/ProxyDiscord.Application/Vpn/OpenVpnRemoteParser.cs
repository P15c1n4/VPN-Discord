using System.Text.RegularExpressions;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Vpn;

public readonly record struct OpenVpnRemote(string Host, int Port, TransportProtocol Transport);

public static partial class OpenVpnRemoteParser
{
    private const int DEFAULT_OPENVPN_PORT = 1194;

    [GeneratedRegex(
        @"^[\t ]*remote[\t ]+(?<host>[^\s#;]+)(?:[\t ]+(?<port>\d{1,5}))?(?:[\t ]+(?<remoteProto>[^\s#;]+))?[\t ]*(?:[#;][^\r\n]*)?\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RemoteDirective();

    [GeneratedRegex(
        @"^[\t ]*proto[\t ]+(?<proto>[^\s#;]*)[\t ]*(?:[#;][^\r\n]*)?\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ProtoDirective();

    [GeneratedRegex(@"^(?:tcp(?:4|6)?(?:-client)?|udp(?:4|6)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ClientProtocol();

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
        var profileDirective = ProtoDirective().Match(config);
        var profileProto = profileDirective.Groups["proto"];
        if (remoteProto.Success && !ClientProtocol().IsMatch(remoteProto.Value) ||
            profileDirective.Success && !ClientProtocol().IsMatch(profileProto.Value))
        {
            return null;
        }
        var protocol = remoteProto.Success ? remoteProto.Value : profileProto.Value;
        var transport = string.IsNullOrEmpty(protocol) || protocol.StartsWith("udp", StringComparison.OrdinalIgnoreCase)
            ? TransportProtocol.Udp
            : TransportProtocol.Tcp;

        return new OpenVpnRemote(remote.Groups["host"].Value, port, transport);
    }
}
