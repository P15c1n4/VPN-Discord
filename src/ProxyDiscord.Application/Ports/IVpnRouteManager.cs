using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IVpnRouteManager
{
    void EnsureTunnelDefaultRoute(VpnAdapterInfo adapter);

    void RemoveTunnelDefaultRoute();

    int RemoveOrphanedRoutes();

    bool HasRoute { get; }
}
