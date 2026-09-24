using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IUserConfigurationStore
{
    Task<UserConfiguration> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(UserConfiguration configuration, CancellationToken cancellationToken = default);
}
