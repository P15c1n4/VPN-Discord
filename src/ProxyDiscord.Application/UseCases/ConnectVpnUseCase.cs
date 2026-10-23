using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
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
    IOutboundInterfaceResolver outboundInterfaceResolver,
    IConnectionStateStore stateStore,
    IProcessLivenessChecker livenessChecker,
    ISystemClock clock,
    RoutingSessionContext sessionContext,
    ILogger<ConnectVpnUseCase> logger,
    TimeSpan? trafficObservationTimeout = null,
    TunnelDiagnostics? diagnostics = null)
{
    private readonly TimeSpan _trafficObservationTimeout = trafficObservationTimeout ?? TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CONNECTION_OPERATION_TIMEOUT = TimeSpan.FromMinutes(5);

    public async Task<VpnConnectionResult> ExecuteAsync(ConnectVpnCommand command, CancellationToken cancellationToken = default)
    {
        if (!await sessionContext.Lifecycle.Gate.WaitAsync(0, cancellationToken))
        {
            return VpnConnectionResult.Failed(VpnLinkStatus.Error,
                "Já existe uma conexão ou limpeza em andamento. Aguarde a conclusão.");
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (sessionContext.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting)
        {
            sessionContext.Lifecycle.Gate.Release();
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, "Desconecte a sessão atual antes de iniciar outra conexão.");
        }
        if (!sessionContext.Lifecycle.TrySetAttempt(attempt))
        {
            sessionContext.Lifecycle.Gate.Release();
            return VpnConnectionResult.Failed(VpnLinkStatus.Disconnected, "A desconexão está em andamento. Aguarde a conclusão.");
        }
        try
        {
            return await ExecuteCoreAsync(command, attempt.Token);
        }
        finally
        {
            sessionContext.Lifecycle.ClearAttempt();
            sessionContext.Lifecycle.Gate.Release();
        }
    }

    private async Task<VpnConnectionResult> ExecuteCoreAsync(ConnectVpnCommand command, CancellationToken cancellationToken)
    {
        sessionContext.SetTargetProcess(command.TargetProcess);
        sessionContext.SetConnecting();
        diagnostics?.Reset();

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(CONNECTION_OPERATION_TIMEOUT);
        var operationToken = operationTimeout.Token;
        var vpnEstablished = false;
        var rollbackAttempted = false;
        var currentStage = "inicialização";

        void SetStage(string stage)
        {
            currentStage = stage;
            logger.LogInformation("Etapa da conexão VPN: {Stage}", stage);
        }

        async Task RollbackConnectionOnceAsync()
        {
            if (!vpnEstablished || rollbackAttempted)
            {
                return;
            }

            rollbackAttempted = true;
            await RollbackAsync(CancellationToken.None);
        }

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
            command.OpenVpnConfigBase64,
            command.UseProfileOpenVpnCredentials);

        try
        {
            // Captura antes de discar: o OpenVPN pode alterar as rotas assim que o túnel sobe.
            // A consulta direta do auto-teste precisa continuar presa à interface física original.
            SetStage("captura da interface física");
            var directInterface = await outboundInterfaceResolver.CapturePhysicalInterfaceAsync(operationToken);

            SetStage("conexão com o servidor VPN");
            var connectResult = await vpnConnection.ConnectAsync(request, operationToken);

            if (!connectResult.Success)
            {
                var reason = connectResult.ErrorMessage ?? "O serviço de VPN não informou o motivo da falha.";
                logger.LogWarning("Conexão VPN recusada: {Reason}", reason);
                sessionContext.SetError(reason);
                return connectResult;
            }

            vpnEstablished = true;
            operationToken.ThrowIfCancellationRequested();

            SetStage("localização do adaptador VPN");
            var adapter = await vpnConnection.GetAdapterInfoAsync(operationToken);
            operationToken.ThrowIfCancellationRequested();
            if (adapter is null)
            {
                const string adapterError = "VPN conectada, mas não foi possível localizar o adaptador de rede correspondente.";
                logger.LogError("{Error}", adapterError);
                await RollbackConnectionOnceAsync();
                sessionContext.SetError(adapterError);
                return VpnConnectionResult.Failed(VpnLinkStatus.Error, adapterError);
            }

            try
            {
                SetStage("configuração da rota do túnel");
                routeManager.EnsureTunnelDefaultRoute(adapter);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao instalar a rota do túnel na interface VPN");
                await RollbackConnectionOnceAsync();
                var error = $"Não foi possível configurar a rota da VPN. Detalhes: {ex.Message}";
                sessionContext.SetError(error);
                return VpnConnectionResult.Failed(VpnLinkStatus.Error, error);
            }

            SetStage("teste de saída pela VPN");
            var selfTest = await egressSelfTest.RunAsync(adapter, directInterface, operationToken);
            operationToken.ThrowIfCancellationRequested();
            if (!selfTest.Success)
            {
                var error = $"A conexão VPN não passou no teste de saída: {selfTest.Summary}";
                logger.LogError("{Error}", error);
                await RollbackConnectionOnceAsync();
                sessionContext.SetError(error);
                return VpnConnectionResult.Failed(VpnLinkStatus.Error, error);
            }

            var target = new TargetProcessSelector(command.TargetProcess.Name, command.TargetProcess.ExecutablePath);

            try
            {
                SetStage("inicialização do roteamento por processo");
                await routingEngine.StartAsync(
                    target,
                    adapter,
                    command.DnsSettings,
                    command.ProtocolScope,
                    command.RoutingBackend,
                    operationToken);
                operationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao iniciar o motor de roteamento por processo");
                await RollbackConnectionOnceAsync();
                var error = $"Não foi possível iniciar o roteamento do processo. Detalhes: {ex.Message}";
                sessionContext.SetError(error);
                return VpnConnectionResult.Failed(VpnLinkStatus.Error, error);
            }

            SetStage("observação inicial do tráfego");
            var trafficObserved = await WaitForTrafficAsync(operationToken);
            operationToken.ThrowIfCancellationRequested();
            if (!trafficObserved)
            {
                logger.LogInformation(
                    "Nenhum tráfego de '{ProcessName}' observado em {Timeout}s. O túnel está armado e " +
                    "passará a rotear assim que o processo iniciar ou gerar tráfego.",
                    target.DisplayName, _trafficObservationTimeout.TotalSeconds);
            }

            var (ownerPid, ownerStartedUtc) = livenessChecker.GetCurrentProcessInfo();
            SetStage("gravação do estado da conexão");
            await stateStore.WriteActiveStateAsync(
                new ConnectionStateRecord(ownerPid, ownerStartedUtc, command.TargetProcess.Pid, command.TargetProcess.Name, entryName, clock.UtcNow),
                operationToken);

            operationToken.ThrowIfCancellationRequested();
            sessionContext.SetConnected(latency: null);
            return VpnConnectionResult.Ok(VpnLinkStatus.Connected);
        }
        catch (OperationCanceledException) when (operationTimeout.IsCancellationRequested)
        {
            await RollbackConnectionOnceAsync();
            var error = cancellationToken.IsCancellationRequested
                ? "A tentativa de conexão foi cancelada."
                : $"A etapa '{currentStage}' excedeu o limite total de {CONNECTION_OPERATION_TIMEOUT.TotalMinutes:0} minutos.";
            logger.LogWarning("{Error} Última etapa: {Stage}.", error, currentStage);
            sessionContext.SetError(error);
            return VpnConnectionResult.Failed(
                cancellationToken.IsCancellationRequested ? VpnLinkStatus.Disconnected : VpnLinkStatus.Error,
                error);
        }
        catch (Exception ex)
        {
            await RollbackConnectionOnceAsync();
            logger.LogError(ex, "Falha inesperada na etapa '{Stage}' da conexão VPN", currentStage);
            var error = $"Falha na etapa '{currentStage}' da conexão VPN. Detalhes: {ex.Message}";
            sessionContext.SetError(error);
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, error);
        }
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
            await routingEngine.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao parar o roteamento durante o rollback");
        }

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
