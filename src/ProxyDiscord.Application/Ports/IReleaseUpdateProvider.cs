using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IReleaseUpdateProvider
{
    Task<UpdateReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default);
}
