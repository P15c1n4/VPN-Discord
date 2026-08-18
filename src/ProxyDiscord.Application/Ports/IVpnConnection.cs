using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IVpnConnection
{
    Task<VpnConnectionResult> ConnectAsync(VpnConnectionRequest request, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<VpnLinkStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<VpnAdapterInfo?> GetAdapterInfoAsync(CancellationToken cancellationToken = default);

    Task ForceDisconnectByNameAsync(string entryName, CancellationToken cancellationToken = default);
}
