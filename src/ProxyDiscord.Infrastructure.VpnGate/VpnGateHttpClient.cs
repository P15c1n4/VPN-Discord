using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.VpnGate;

internal sealed class VpnGateHttpClient(HttpClient httpClient, ILogger<VpnGateHttpClient> logger)
{
    private const string FEED_URL = "https://www.vpngate.net/api/iphone/";

    private const string SERVER_LIST_URL = "https://www.vpngate.net/en/";

    public async Task<string> DownloadCsvAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(FEED_URL, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string?> TryDownloadServerListHtmlAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(SERVER_LIST_URL, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                "Não foi possível baixar a página do VPN Gate ({Message}); a lista ficará sem a informação de MS-SSTP.",
                ex.Message);
            return null;
        }
    }
}
