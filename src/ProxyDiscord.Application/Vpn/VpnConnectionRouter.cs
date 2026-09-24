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

    private IVpnProvider? _active;

    public event EventHandler<VpnConnectionLostEventArgs>? ConnectionLost;

    public async Task<VpnConnectionResult> ConnectAsync(
        VpnConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(request.Protocol, out var provider))
        {
            return VpnConnectionResult.Failed(
                VpnLinkStatus.Error, $"O protocolo {request.Protocol.DisplayName()} não é compatível com esta versão do aplicativo.");
        }

        if (_active is not null)
        {
            await DisconnectAsync(cancellationToken);
        }

        logger.LogInformation(
            "Conectando via {Protocol} em {Endpoint}", request.Protocol.DisplayName(), request.Endpoint);

        var result = await provider.ConnectAsync(request, cancellationToken);
        if (result.Success)
        {
            provider.ConnectionLost += OnProviderConnectionLost;
            _active = provider;
        }
        else
        {
            _active = null;
        }

        return result;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var active = _active;
        _active = null;

        if (active is not null)
        {
            active.ConnectionLost -= OnProviderConnectionLost;
            await active.DisconnectAsync(cancellationToken);
        }
    }

    private void OnProviderConnectionLost(object? sender, VpnConnectionLostEventArgs args)
    {
        if (sender is IVpnProvider provider && ReferenceEquals(provider, _active))
        {
            ConnectionLost?.Invoke(this, args);
        }
    }

    public Task<VpnLinkStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _active?.GetStatusAsync(cancellationToken) ?? Task.FromResult(VpnLinkStatus.Disconnected);

    public Task<VpnAdapterInfo?> GetAdapterInfoAsync(CancellationToken cancellationToken = default) =>
        _active?.GetAdapterInfoAsync(cancellationToken) ?? Task.FromResult<VpnAdapterInfo?>(null);

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
}
