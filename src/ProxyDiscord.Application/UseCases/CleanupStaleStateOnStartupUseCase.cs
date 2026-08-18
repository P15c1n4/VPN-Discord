using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Application.UseCases;

public sealed class CleanupStaleStateOnStartupUseCase(
    IConnectionStateStore stateStore,
    IVpnConnection vpnConnection,
    IVpnRouteManager routeManager,
    IProcessLivenessChecker livenessChecker,
    ILogger<CleanupStaleStateOnStartupUseCase> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var stale = await stateStore.TryReadStaleStateAsync(cancellationToken);
        if (stale is null)
        {
            return;
        }

        if (livenessChecker.IsSameProcessStillRunning(stale.OwnerPid, stale.OwnerStartedUtc))
        {
            logger.LogInformation("Estado persistido pertence a uma instância ainda em execução; nada a limpar.");
            return;
        }

        logger.LogWarning(
            "Estado órfão encontrado (processo dono PID {Pid} não está mais em execução). Limpando conexão VPN '{Entry}'.",
            stale.OwnerPid, stale.RasEntryName);

        try
        {
            await vpnConnection.ForceDisconnectByNameAsync(stale.RasEntryName, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao limpar a conexão VPN órfã '{Entry}'", stale.RasEntryName);
        }

        try
        {
            var removed = routeManager.RemoveOrphanedRoutes();
            if (removed > 0)
            {
                logger.LogInformation("{Count} rota(s) de túnel órfã(s) removida(s).", removed);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao remover rotas de túnel órfãs");
        }

        await stateStore.ClearStateAsync(cancellationToken);
    }
}
