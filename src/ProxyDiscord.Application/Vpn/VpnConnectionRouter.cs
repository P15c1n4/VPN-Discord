using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Vpn;

public sealed class VpnConnectionRouter(
    IEnumerable<IVpnProvider> providers,
    ILogger<VpnConnectionRouter> logger) : IVpnConnection
{
    private readonly IReadOnlyDictionary<VpnProtocol, IVpnProvider> _providers =
        providers.ToDictionary(provider => provider.Protocol);

    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly object _stateLock = new();
    private IVpnProvider? _active;
    private CancellationTokenSource? _connectCancellation;

    public event EventHandler<VpnConnectionLostEventArgs>? ConnectionLost;

    public async Task<VpnConnectionResult> ConnectAsync(
        VpnConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(request.Protocol, out var provider))
        {
            return VpnConnectionResult.Failed(
                VpnLinkStatus.Error, $"O protocolo {request.Protocol.DisplayName()} não é compatível com esta versão do aplicativo.");
        }

        if (!await _transitionGate.WaitAsync(0, cancellationToken))
        {
            return VpnConnectionResult.Failed(
                VpnLinkStatus.Error,
                "Já existe uma operação de conexão ou desconexão em andamento. Aguarde e tente novamente.");
        }

        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateLock)
        {
            _connectCancellation = attemptCancellation;
        }

        var providerConnectStarted = false;
        try
        {
            await DisconnectActiveCoreAsync();
            logger.LogInformation(
                "Conectando via {Protocol} em {Endpoint}", request.Protocol.DisplayName(), request.Endpoint);

            providerConnectStarted = true;
            var result = await provider.ConnectAsync(request, attemptCancellation.Token);
            if (!result.Success || attemptCancellation.IsCancellationRequested)
            {
                await DisconnectProviderSafelyAsync(provider);
                return attemptCancellation.IsCancellationRequested
                    ? VpnConnectionResult.Failed(VpnLinkStatus.Disconnected, "A tentativa de conexão foi cancelada.")
                    : result;
            }

            lock (_stateLock)
            {
                provider.ConnectionLost += OnProviderConnectionLost;
                _active = provider;
            }

            return result;
        }
        catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
        {
            if (providerConnectStarted)
            {
                await DisconnectProviderSafelyAsync(provider);
            }

            return VpnConnectionResult.Failed(VpnLinkStatus.Disconnected, "A tentativa de conexão foi cancelada.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao conectar via {Protocol}", request.Protocol.DisplayName());
            if (providerConnectStarted)
            {
                await DisconnectProviderSafelyAsync(provider);
            }

            return VpnConnectionResult.Failed(
                VpnLinkStatus.Error,
                $"Não foi possível conectar pela VPN. Detalhes: {ex.Message}");
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_connectCancellation, attemptCancellation))
                {
                    _connectCancellation = null;
                }
            }

            _transitionGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? pending;
        lock (_stateLock)
        {
            pending = _connectCancellation;
        }

        if (pending is not null)
        {
            try
            {
                await pending.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // A conexão encerrou e liberou o token entre a leitura do estado e o cancelamento.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao sinalizar o cancelamento da tentativa VPN pendente");
            }
        }

        await _transitionGate.WaitAsync(CancellationToken.None);
        try
        {
            await DisconnectActiveCoreAsync();
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private void OnProviderConnectionLost(object? sender, VpnConnectionLostEventArgs args)
    {
        lock (_stateLock)
        {
            if (sender is IVpnProvider provider && ReferenceEquals(provider, _active))
            {
                ConnectionLost?.Invoke(this, args);
            }
        }
    }

    public Task<VpnLinkStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            return _active?.GetStatusAsync(cancellationToken) ?? Task.FromResult(VpnLinkStatus.Disconnected);
        }
    }

    public Task<VpnAdapterInfo?> GetAdapterInfoAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            return _active?.GetAdapterInfoAsync(cancellationToken) ?? Task.FromResult<VpnAdapterInfo?>(null);
        }
    }

    public async Task ForceDisconnectByNameAsync(string entryName, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers.Values)
        {
            try
            {
                await provider.ForceDisconnectByNameAsync(entryName, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Falha na limpeza de '{Entry}' pelo provedor {Protocol}", entryName, provider.Protocol);
            }
        }
    }

    private async Task DisconnectActiveCoreAsync()
    {
        IVpnProvider? active;
        lock (_stateLock)
        {
            active = _active;
            _active = null;
        }

        if (active is null)
        {
            return;
        }

        active.ConnectionLost -= OnProviderConnectionLost;
        try
        {
            await active.DisconnectAsync(CancellationToken.None);
        }
        catch
        {
            lock (_stateLock)
            {
                _active = active;
                active.ConnectionLost += OnProviderConnectionLost;
            }

            throw;
        }
    }

    private async Task DisconnectProviderSafelyAsync(IVpnProvider provider)
    {
        try
        {
            await provider.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao limpar uma tentativa incompleta do provedor {Protocol}", provider.Protocol);
        }
    }
}
