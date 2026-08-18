using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.ProcessManagement;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProcessManagement(this IServiceCollection services)
    {
        services.AddSingleton<IProcessRepository, Win32ProcessRepository>();
        services.AddSingleton<IProcessLivenessChecker, Win32ProcessLivenessChecker>();
        return services;
    }
}
