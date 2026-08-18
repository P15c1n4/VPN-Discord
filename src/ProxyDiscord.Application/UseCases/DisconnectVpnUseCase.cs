using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;

namespace ProxyDiscord.Application.UseCases;

public sealed class DisconnectVpnUseCase(
    IProcessRoutingEngine routingEngine,
    IVpnConnection vpnConnection,
    IVpnRouteManager routeManager,
    IConnectionStateStore stateStore,
    RoutingSessionContext sessionContext,
    ILogger<DisconnectVpnUseCase> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await routingEngine.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao parar o motor de roteamento (pode já estar parado)");
        }

        try
        {
            routeManager.RemoveTunnelDefaultRoute();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao remover a rota do túnel (pode já ter sido removida)");
        }

        try
        {
            await vpnConnection.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao desconectar a VPN (pode já estar desconectada)");
        }

        try
        {
            await stateStore.ClearStateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao limpar o estado persistido");
        }

        sessionContext.SetIdle();
    }
}
