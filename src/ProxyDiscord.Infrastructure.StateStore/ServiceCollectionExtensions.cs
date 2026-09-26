using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.StateStore;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConnectionStateStore(this IServiceCollection services)
    {
        services.AddSingleton<IConnectionStateStore, FileConnectionStateStore>();
        services.AddSingleton<JsonApplicationDataStore>();
        services.AddSingleton<IUserConfigurationStore>(sp => sp.GetRequiredService<JsonApplicationDataStore>());
        services.AddSingleton<IServerCredentialsStore>(sp => sp.GetRequiredService<JsonApplicationDataStore>());
        services.AddSingleton<ISystemClock, SystemClock>();
        return services;
    }
}
