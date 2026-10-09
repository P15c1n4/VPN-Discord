using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.UseCases;

public sealed class VpnConnectionSupervisor : IDisposable
{
    private readonly IVpnConnection _vpnConnection;
    private readonly IProcessRoutingEngine _routingEngine;
    private readonly DisconnectVpnUseCase _disconnectVpnUseCase;
    private readonly RoutingSessionContext _sessionContext;
    private readonly ILogger<VpnConnectionSupervisor> _logger;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);

    public VpnConnectionSupervisor(
        IVpnConnection vpnConnection,
        IProcessRoutingEngine routingEngine,
        DisconnectVpnUseCase disconnectVpnUseCase,
        RoutingSessionContext sessionContext,
        ILogger<VpnConnectionSupervisor> logger)
    {
        _vpnConnection = vpnConnection;
        _routingEngine = routingEngine;
        _disconnectVpnUseCase = disconnectVpnUseCase;
        _sessionContext = sessionContext;
        _logger = logger;
        _vpnConnection.ConnectionLost += OnConnectionLost;
        _routingEngine.Failed += OnRoutingEngineFailed;
    }

    private async void OnConnectionLost(object? sender, VpnConnectionLostEventArgs args)
        => await HandleFailureAsync(args.Reason, "VPN");

    private async void OnRoutingEngineFailed(object? sender, RoutingEngineFailureEventArgs args)
        => await HandleFailureAsync(args.Reason, "roteamento");

    private async Task HandleFailureAsync(string reason, string component)
    {
        if (_sessionContext.Status is not (ConnectionStatus.Connecting or ConnectionStatus.Connected))
        {
            return;
        }

        await _cleanupGate.WaitAsync();
        try
        {
            if (_sessionContext.Status is not (ConnectionStatus.Connecting or ConnectionStatus.Connected))
            {
                return;
            }

            _logger.LogWarning("A sessão perdeu o componente {Component}; iniciando limpeza automática: {Reason}", component, reason);
            await _disconnectVpnUseCase.ExecuteAsync(CancellationToken.None);
            _sessionContext.SetError(component == "VPN"
                ? $"A conexão VPN foi interrompida. Detalhes: {reason}"
                : $"O roteamento foi interrompido. Detalhes: {reason}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao limpar a sessão após a falha de {Component}", component);
            _sessionContext.SetError($"A sessão foi interrompida e a limpeza não foi concluída. Detalhes: {reason}");
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    public void Dispose()
    {
        _vpnConnection.ConnectionLost -= OnConnectionLost;
        _routingEngine.Failed -= OnRoutingEngineFailed;
        _cleanupGate.Dispose();
    }
}
