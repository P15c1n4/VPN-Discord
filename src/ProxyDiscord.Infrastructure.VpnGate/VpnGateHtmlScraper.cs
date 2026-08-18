using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.VpnGate;

internal readonly record struct VpnGateProtocolSupport(string HostName, string? SstpHostName);

internal sealed partial class VpnGateHtmlScraper(ILogger<VpnGateHtmlScraper> logger)
{
    [GeneratedRegex(@"<tr>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"(?<host>[A-Za-z0-9\-]+)\.opengw\.net", RegexOptions.IgnoreCase)]
    private static partial Regex DdnsHostName();

    [GeneratedRegex(@"SSTP\s+Hostname\s*:.*?>(?<host>[A-Za-z0-9\-]+\.opengw\.net)<",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SstpHostName();

    public IReadOnlyDictionary<string, VpnGateProtocolSupport> Parse(string html)
    {
        var support = new Dictionary<string, VpnGateProtocolSupport>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in TableRow().Matches(html).Cast<Match>())
        {
            var row = match.Groups["row"].Value;

            var hostMatch = DdnsHostName().Match(row);
            if (!hostMatch.Success)
            {
                continue;
            }

            var hostName = hostMatch.Groups["host"].Value;
            var sstpMatch = SstpHostName().Match(row);
            var sstpHost = sstpMatch.Success ? sstpMatch.Groups["host"].Value : null;

            support[hostName] = new VpnGateProtocolSupport(hostName, sstpHost);
        }

        logger.LogDebug(
            "Página do VPN Gate: {Total} servidores identificados, {Sstp} com MS-SSTP.",
            support.Count, support.Values.Count(entry => entry.SstpHostName is not null));

        return support;
    }
}
