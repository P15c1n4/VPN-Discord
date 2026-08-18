using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Application.UseCases;

public sealed class FetchVpnGateListUseCase(IVpnGateClient vpnGateClient)
{
    public Task<IReadOnlyList<VpnGateServerEntry>> ExecuteAsync(CancellationToken cancellationToken = default) =>
        vpnGateClient.GetServerListAsync(cancellationToken);
}
