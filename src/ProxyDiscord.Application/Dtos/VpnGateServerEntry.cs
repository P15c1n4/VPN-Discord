using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Dtos;

public sealed record VpnGateServerEntry(
    string HostName,
    string IpAddress,
    HostEndpoint? OpenVpnEndpoint,
    TransportProtocol? OpenVpnTransport,
    HostEndpoint? SstpEndpoint,
    string OpenVpnConfigBase64,
    int Score,
    int PingMs,
    long SpeedBps,
    string CountryLong,
    string CountryShort,
    int NumVpnSessions,
    long Uptime,
    int TotalUsers,
    long TotalTraffic,
    string Operator,
    string Message)
{
    public bool SupportsOpenVpn => OpenVpnEndpoint is not null && !string.IsNullOrWhiteSpace(OpenVpnConfigBase64);

    public bool SupportsSstp => SstpEndpoint is not null;

    public bool IsSupported => SupportsOpenVpn || SupportsSstp;

    public IReadOnlyList<VpnProtocol> SupportedProtocols
    {
        get
        {
            var protocols = new List<VpnProtocol>(2);
            if (SupportsOpenVpn)
            {
                protocols.Add(VpnProtocol.OpenVpn);
            }

            if (SupportsSstp)
            {
                protocols.Add(VpnProtocol.Sstp);
            }

            return protocols;
        }
    }

    public VpnProtocol PreferredProtocol => SupportsOpenVpn ? VpnProtocol.OpenVpn : VpnProtocol.Sstp;

    public HostEndpoint? EndpointFor(VpnProtocol protocol) =>
        protocol == VpnProtocol.OpenVpn ? OpenVpnEndpoint : SstpEndpoint;

    public string ProtocolSummary
    {
        get
        {
            var parts = new List<string>(2);
            if (SupportsOpenVpn)
            {
                parts.Add(OpenVpnTransport is { } transport
                    ? $"OpenVPN ({transport.ToString().ToUpperInvariant()})"
                    : "OpenVPN");
            }

            if (SupportsSstp)
            {
                parts.Add("MS-SSTP");
            }

            return string.Join(", ", parts);
        }
    }
}
