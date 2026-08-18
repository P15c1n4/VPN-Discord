using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.VpnGate;

internal sealed class VpnGateCsvParser(ILogger<VpnGateCsvParser> logger)
{
    private const int EXPECTED_COLUMN_COUNT = 15;

    public IReadOnlyList<VpnGateCsvRow> Parse(string csv)
    {
        var rows = new List<VpnGateCsvRow>();

        foreach (var line in csv.Split('\n'))
        {
            var trimmed = line.Trim('\r', ' ');

            if (trimmed.Length == 0 || trimmed.StartsWith('*') || trimmed.StartsWith('#'))
            {
                continue;
            }

            var columns = trimmed.Split(',');
            if (columns.Length < EXPECTED_COLUMN_COUNT)
            {
                logger.LogWarning("Ignorando linha malformada da lista VPN Gate ({Count} colunas)", columns.Length);
                continue;
            }

            var openVpnConfigBase64 = columns[^1];
            var message = string.Join(',', columns[13..^1]);

            rows.Add(new VpnGateCsvRow(
                HostName: columns[0],
                Ip: columns[1],
                Score: columns[2],
                PingMs: columns[3],
                SpeedBps: columns[4],
                CountryLong: columns[5],
                CountryShort: columns[6],
                NumVpnSessions: columns[7],
                Uptime: columns[8],
                TotalUsers: columns[9],
                TotalTraffic: columns[10],
                LogType: columns[11],
                Operator: columns[12],
                Message: message,
                OpenVpnConfigBase64: openVpnConfigBase64));
        }

        return rows;
    }
}
