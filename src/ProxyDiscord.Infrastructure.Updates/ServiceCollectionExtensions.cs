using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Updates;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReleaseUpdates(this IServiceCollection services)
    {
        services.AddHttpClient<IReleaseUpdateProvider, GitHubReleaseUpdateProvider>(client =>
            client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IUpdateProcessLauncher, UpdaterProcessLauncher>();
        return services;
    }
}
