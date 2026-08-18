using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Routing;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProcessRouting(this IServiceCollection services)
    {
        services.AddSingleton<IIpHelperTableReader, IpHelperTableReader>();
        services.AddSingleton<IProcessGroupWatcher, ProcessGroupWatcher>();
        services.AddSingleton<FlowRegistry>();
        services.AddSingleton<TcpTunnelRelay>();
        services.AddSingleton<UdpTunnelRelay>();
        services.AddSingleton<IProcessRoutingEngine, ProcessRoutingEngine>();
        services.AddSingleton<IVpnRouteManager, VpnRouteManager>();
        services.AddSingleton<IVpnEgressSelfTest, VpnEgressSelfTest>();
        return services;
    }
}
