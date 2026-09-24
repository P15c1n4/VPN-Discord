using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.UseCases;

public sealed class VpnConnectionSupervisor : IDisposable
{
    private readonly IVpnConnection _vpnConnection;
    private readonly DisconnectVpnUseCase _disconnectVpnUseCase;
    private readonly RoutingSessionContext _sessionContext;
    private readonly ILogger<VpnConnectionSupervisor> _logger;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);

    public VpnConnectionSupervisor(
        IVpnConnection vpnConnection,
        DisconnectVpnUseCase disconnectVpnUseCase,
        RoutingSessionContext sessionContext,
        ILogger<VpnConnectionSupervisor> logger)
    {
        _vpnConnection = vpnConnection;
        _disconnectVpnUseCase = disconnectVpnUseCase;
        _sessionContext = sessionContext;
        _logger = logger;
        _vpnConnection.ConnectionLost += OnConnectionLost;
    }

    private async void OnConnectionLost(object? sender, VpnConnectionLostEventArgs args)
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

            _logger.LogWarning("A sessão VPN perdeu o túnel; iniciando limpeza automática: {Reason}", args.Reason);
            await _disconnectVpnUseCase.ExecuteAsync(CancellationToken.None);
            _sessionContext.SetError($"A conexão VPN foi interrompida. Detalhes: {args.Reason}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao limpar a sessão após queda da VPN");
            _sessionContext.SetError($"A conexão VPN foi interrompida e a limpeza não foi concluída. Detalhes: {args.Reason}");
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    public void Dispose()
    {
        _vpnConnection.ConnectionLost -= OnConnectionLost;
        _cleanupGate.Dispose();
    }
}
