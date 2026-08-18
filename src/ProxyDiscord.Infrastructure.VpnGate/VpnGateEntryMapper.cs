using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.VpnGate;

internal sealed class VpnGateEntryMapper(ILogger<VpnGateEntryMapper> logger)
{
    private const string DDNS_SUFFIX = ".opengw.net";

    private const int SSTP_PORT = 443;

    public IReadOnlyList<VpnGateServerEntry> Map(
        IReadOnlyList<VpnGateCsvRow> rows,
        IReadOnlyDictionary<string, VpnGateProtocolSupport> protocolSupport)
    {
        var result = new List<VpnGateServerEntry>(rows.Count);
        var skipped = 0;

        foreach (var row in rows)
        {
            try
            {
                var entry = MapRow(row, protocolSupport);

                if (!entry.IsSupported)
                {
                    skipped++;
                    continue;
                }

                result.Add(entry);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Ignorando servidor VPN Gate '{Host}': não foi possível interpretar os dados", row.HostName);
            }
        }

        logger.LogInformation(
            "Lista VPN Gate: {Shown} servidores compatíveis ({OpenVpn} OpenVPN, {Sstp} MS-SSTP), {Skipped} descartados por não oferecerem nenhum protocolo suportado.",
            result.Count,
            result.Count(entry => entry.SupportsOpenVpn),
            result.Count(entry => entry.SupportsSstp),
            skipped);

        return result;
    }

    private VpnGateServerEntry MapRow(
        VpnGateCsvRow row, IReadOnlyDictionary<string, VpnGateProtocolSupport> protocolSupport)
    {
        var hostName = NormalizeHostName(row.HostName);
        var profile = OpenVpnConfigReader.TryRead(row.OpenVpnConfigBase64);

        HostEndpoint? openVpnEndpoint = null;
        TransportProtocol? openVpnTransport = null;
        if (profile is { } info)
        {
            openVpnEndpoint = HostEndpoint.Create(
                string.IsNullOrWhiteSpace(info.Host) ? hostName : info.Host, info.Port);
            openVpnTransport = info.Transport;
        }

        var sstpEndpoint = ResolveSstpEndpoint(row.HostName, hostName, protocolSupport);

        return new VpnGateServerEntry(
            HostName: row.HostName,
            IpAddress: row.Ip,
            OpenVpnEndpoint: openVpnEndpoint,
            OpenVpnTransport: openVpnTransport,
            SstpEndpoint: sstpEndpoint,
            OpenVpnConfigBase64: row.OpenVpnConfigBase64,
            Score: ParseInt(row.Score),
            PingMs: ParseInt(row.PingMs),
            SpeedBps: ParseLong(row.SpeedBps),
            CountryLong: row.CountryLong,
            CountryShort: row.CountryShort,
            NumVpnSessions: ParseInt(row.NumVpnSessions),
            Uptime: ParseLong(row.Uptime),
            TotalUsers: ParseInt(row.TotalUsers),
            TotalTraffic: ParseLong(row.TotalTraffic),
            Operator: row.Operator,
            Message: row.Message);
    }

    private static HostEndpoint? ResolveSstpEndpoint(
        string rawHostName, string ddnsHostName, IReadOnlyDictionary<string, VpnGateProtocolSupport> protocolSupport)
    {
        var key = rawHostName.Trim();
        if (key.EndsWith(DDNS_SUFFIX, StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^DDNS_SUFFIX.Length];
        }

        if (!protocolSupport.TryGetValue(key, out var support) || support.SstpHostName is null)
        {
            return null;
        }

        return HostEndpoint.Create(support.SstpHostName, SSTP_PORT);
    }

    private static string NormalizeHostName(string rawHostName)
    {
        var trimmed = rawHostName.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("A linha não tem HostName, então não há endereço utilizável.");
        }

        return trimmed.EndsWith(DDNS_SUFFIX, StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + DDNS_SUFFIX;
    }

    private static int ParseInt(string value) => int.TryParse(value, out var parsed) ? parsed : 0;

    private static long ParseLong(string value) => long.TryParse(value, out var parsed) ? parsed : 0L;
}
