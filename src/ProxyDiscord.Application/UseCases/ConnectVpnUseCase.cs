using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Domain.Exceptions;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.UseCases;

public sealed class ConnectVpnUseCase(
    IVpnConnection vpnConnection,
    IProcessRoutingEngine routingEngine,
    IVpnRouteManager routeManager,
    IVpnEgressSelfTest egressSelfTest,
    IConnectionStateStore stateStore,
    IProcessLivenessChecker livenessChecker,
    ISystemClock clock,
    RoutingSessionContext sessionContext,
    ILogger<ConnectVpnUseCase> logger,
    TimeSpan? trafficObservationTimeout = null)
{
    private readonly TimeSpan _trafficObservationTimeout = trafficObservationTimeout ?? TimeSpan.FromSeconds(10);

    public async Task<VpnConnectionResult> ExecuteAsync(ConnectVpnCommand command, CancellationToken cancellationToken = default)
    {
        sessionContext.SetTargetProcess(command.TargetProcess);
        sessionContext.SetConnecting();

        HostEndpoint endpoint;
        try
        {
            endpoint = HostEndpoint.Parse(command.ServerAddressRaw, command.Protocol.DefaultPort());
        }
        catch (AddressParseException ex)
        {
            sessionContext.SetError(ex.Message);
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, ex.Message);
        }

        var entryName = BuildEntryName(command.TargetProcess.Name);
        var request = new VpnConnectionRequest(
            endpoint,
            command.Protocol,
            command.Username,
            command.Password,
            entryName,
            command.OpenVpnConfigBase64);

        VpnConnectionResult connectResult;
        try
        {
            connectResult = await vpnConnection.ConnectAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha inesperada ao discar a VPN");
            sessionContext.SetError($"Falha ao conectar VPN: {ex.Message}");
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, ex.Message);
        }

        if (!connectResult.Success)
        {
            var reason = connectResult.ErrorMessage ?? "Falha desconhecida ao conectar a VPN.";
            logger.LogWarning("Conexão VPN recusada: {Reason}", reason);
            sessionContext.SetError(reason);
            return connectResult;
        }

        var adapter = await vpnConnection.GetAdapterInfoAsync(cancellationToken);
        if (adapter is null)
        {
            logger.LogError("VPN conectada mas o adaptador de rede não foi localizado");
            await RollbackAsync(cancellationToken);
            const string ADAPTER_ERROR = "VPN conectada, mas não foi possível localizar o adaptador de rede correspondente.";
            sessionContext.SetError(ADAPTER_ERROR);
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, ADAPTER_ERROR);
        }

        try
        {
            routeManager.EnsureTunnelDefaultRoute(adapter);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao instalar a rota do túnel na interface VPN");
            await RollbackAsync(cancellationToken);
            sessionContext.SetError($"Falha ao configurar a rota do túnel: {ex.Message}");
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, ex.Message);
        }

        var selfTest = await egressSelfTest.RunAsync(adapter, cancellationToken);
        if (!selfTest.Success)
        {
            var error = $"A VPN conectou, mas o tráfego não sai por ela: {selfTest.Summary}";
            logger.LogError("{Error}", error);
            await RollbackAsync(cancellationToken);
            sessionContext.SetError(error);
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, error);
        }

        var target = new TargetProcessSelector(command.TargetProcess.Name, command.TargetProcess.ExecutablePath);

        try
        {
            await routingEngine.StartAsync(target, adapter, command.DnsSettings, command.ProtocolScope, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao iniciar o motor de roteamento por processo");
            await RollbackAsync(cancellationToken);
            sessionContext.SetError($"Falha ao iniciar roteamento: {ex.Message}");
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, ex.Message);
        }

        var trafficObserved = await WaitForTrafficAsync(cancellationToken);
        if (!trafficObserved)
        {
            logger.LogInformation(
                "Nenhum tráfego de '{ProcessName}' observado em {Timeout}s. O túnel está armado e " +
                "passará a rotear assim que o processo iniciar ou gerar tráfego.",
                target.DisplayName, _trafficObservationTimeout.TotalSeconds);
        }

        var (ownerPid, ownerStartedUtc) = livenessChecker.GetCurrentProcessInfo();
        await stateStore.WriteActiveStateAsync(
            new ConnectionStateRecord(ownerPid, ownerStartedUtc, command.TargetProcess.Pid, command.TargetProcess.Name, entryName, clock.UtcNow),
            cancellationToken);

        sessionContext.SetConnected(latency: null);
        return VpnConnectionResult.Ok(VpnLinkStatus.Connected);
    }

    private static string BuildEntryName(string processName)
    {
        var safeName = new string(processName.Where(char.IsLetterOrDigit).Take(16).ToArray());
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        return $"Discord-VPN-{safeName}-{uniqueSuffix}";
    }

    private Task<bool> WaitForTrafficAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTrafficObserved(object? sender, EventArgs args) => tcs.TrySetResult(true);

        routingEngine.TrafficObserved += OnTrafficObserved;

        return WaitAndUnsubscribe();

        async Task<bool> WaitAndUnsubscribe()
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_trafficObservationTimeout);
                await using var registration = timeoutCts.Token.Register(() => tcs.TrySetResult(false));
                return await tcs.Task;
            }
            finally
            {
                routingEngine.TrafficObserved -= OnTrafficObserved;
            }
        }
    }

    private async Task RollbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            routeManager.RemoveTunnelDefaultRoute();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao remover a rota do túnel durante o rollback");
        }

        try
        {
            await vpnConnection.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao desfazer a conexão VPN durante o rollback");
        }
    }
}
