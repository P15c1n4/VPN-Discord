using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IVpnGateClient
{
    Task<IReadOnlyList<VpnGateServerEntry>> GetServerListAsync(CancellationToken cancellationToken = default);
}
