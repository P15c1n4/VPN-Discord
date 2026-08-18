using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Ras;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSstpVpnManagement(this IServiceCollection services)
    {
        services.AddSingleton<PowerShellProcessRunner>();
        services.AddSingleton<RasDialRunner>();
        services.AddSingleton<VpnAdapterLocator>();
        services.AddSingleton<IVpnProvider, SstpVpnConnection>();
        return services;
    }
}
