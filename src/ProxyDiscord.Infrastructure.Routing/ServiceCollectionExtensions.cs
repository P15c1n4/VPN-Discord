using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
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
        services.AddSingleton<ProcessRoutingEngine>();
        services.AddSingleton<LocalSocks5VpnEndpoint>();
        services.AddSingleton<ProxiFyreRoutingEngine>(sp => new ProxiFyreRoutingEngine(
            sp.GetRequiredService<LocalSocks5VpnEndpoint>(),
            sp.GetRequiredService<TunnelDiagnostics>(),
            sp.GetRequiredService<ILogger<ProxiFyreRoutingEngine>>()));
        services.AddSingleton<IProcessRoutingEngine, ProcessRoutingEngineSelector>();
        services.AddSingleton<IVpnRouteManager, VpnRouteManager>();
        services.AddSingleton<IVpnEgressSelfTest, VpnEgressSelfTest>();
        services.AddSingleton<IOutboundInterfaceResolver, OutboundInterfaceResolver>();
        services.AddSingleton<IVpnInterfaceGatewayResolver, VpnInterfaceGatewayResolver>();
        return services;
    }
}
