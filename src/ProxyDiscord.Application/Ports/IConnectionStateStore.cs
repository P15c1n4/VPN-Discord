using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IConnectionStateStore
{
    Task WriteActiveStateAsync(ConnectionStateRecord record, CancellationToken cancellationToken = default);

    Task ClearStateAsync(CancellationToken cancellationToken = default);

    Task<ConnectionStateRecord?> TryReadStaleStateAsync(CancellationToken cancellationToken = default);
}
