using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.VpnGate;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVpnGateIntegration(this IServiceCollection services)
    {
        services.AddHttpClient<VpnGateHttpClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<VpnGateCsvParser>();
        services.AddSingleton<VpnGateHtmlScraper>();
        services.AddSingleton<VpnGateEntryMapper>();
        services.AddSingleton<IVpnGateClient, VpnGateClient>();
        return services;
    }
}
