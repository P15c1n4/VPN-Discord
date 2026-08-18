using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.OpenVpn;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenVpnManagement(this IServiceCollection services)
    {
        services.AddSingleton<OpenVpnBinaries>();
        services.AddSingleton<TapAdapterProvisioner>();
        services.AddSingleton<OpenVpnProfileWriter>();
        services.AddSingleton<IVpnProvider, OpenVpnConnection>();
        services.AddSingleton<IOpenVpnProfileSource, LocalOpenVpnProfileSource>();
        return services;
    }
}
