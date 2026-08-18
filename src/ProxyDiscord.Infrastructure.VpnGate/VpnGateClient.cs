using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.VpnGate;

internal sealed class VpnGateClient(
    VpnGateHttpClient httpClient,
    VpnGateCsvParser parser,
    VpnGateHtmlScraper scraper,
    VpnGateEntryMapper mapper) : IVpnGateClient
{
    public async Task<IReadOnlyList<VpnGateServerEntry>> GetServerListAsync(CancellationToken cancellationToken = default)
    {
        var csvTask = httpClient.DownloadCsvAsync(cancellationToken);
        var htmlTask = httpClient.TryDownloadServerListHtmlAsync(cancellationToken);

        var rows = parser.Parse(await csvTask);
        var html = await htmlTask;
        var support = html is null
            ? new Dictionary<string, VpnGateProtocolSupport>()
            : scraper.Parse(html);

        return mapper.Map(rows, support);
    }
}
