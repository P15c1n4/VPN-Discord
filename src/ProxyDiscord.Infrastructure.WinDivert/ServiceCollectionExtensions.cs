using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Infrastructure.Routing;

namespace ProxyDiscord.Infrastructure.WinDivert;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWinDivertPacketCapture(this IServiceCollection services)
    {
        services.AddSingleton<IWinDivertHandleFactory, WinDivertHandleFactory>();
        return services;
    }
}
