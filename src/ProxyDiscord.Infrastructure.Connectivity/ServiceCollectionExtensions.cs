using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Connectivity;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConnectivityTesting(this IServiceCollection services)
    {
        services.AddSingleton<IPingService, SystemPingService>();
        return services;
    }
}
