using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Application.UseCases;

public sealed class CheckForUpdatesUseCase(
    IReleaseUpdateProvider releaseProvider,
    ILogger<CheckForUpdatesUseCase> logger)
{
    public async Task<UpdateCheckResult> ExecuteAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        var latest = await releaseProvider.GetLatestAsync(cancellationToken);
        if (latest is not null && latest.Version <= currentVersion)
        {
            logger.LogDebug(
                "O aplicativo está atualizado ({CurrentVersion}; release mais recente: {LatestVersion}).",
                currentVersion, latest.Version);
        }

        return new UpdateCheckResult(currentVersion, latest);
    }
}
