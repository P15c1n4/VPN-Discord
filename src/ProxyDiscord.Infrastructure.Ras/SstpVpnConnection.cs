using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Ras;

internal sealed class SstpVpnConnection(
    PowerShellProcessRunner scriptRunner,
    RasDialRunner rasDial,
    VpnAdapterLocator adapterLocator,
    ILogger<SstpVpnConnection> logger) : IVpnProvider
{
    public VpnProtocol Protocol => VpnProtocol.Sstp;

    private static readonly TimeSpan CONNECT_TIMEOUT = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromMilliseconds(500);

    private string? _activeEntryName;

    public async Task<VpnConnectionResult> ConnectAsync(VpnConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var entryName = request.EntryNameHint;
        var serverAddress = BuildServerAddress(request);

        try
        {
            await scriptRunner.RunAsync(new Dictionary<string, string?>
            {
                ["Action"] = "Create",
                ["Name"] = entryName,
                ["ServerAddress"] = serverAddress,
                ["TunnelType"] = "Sstp",
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao criar a entrada de VPN '{Entry}'", entryName);
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, $"Falha ao configurar a conexão VPN: {ex.Message}");
        }

        var dialed = await rasDial.DialAsync(entryName, request.Username, request.Password, cancellationToken);
        if (!dialed)
        {
            await CleanupEntryAsync(entryName, CancellationToken.None);
            return VpnConnectionResult.Failed(
                VpnLinkStatus.Error,
                "O Windows recusou a discagem da VPN MS-SSTP. Verifique o servidor, a porta, o usuário e a senha.");
        }

        var connected = await WaitUntilUpAsync(entryName, cancellationToken);
        if (!connected)
        {
            await rasDial.HangUpAsync(entryName, CancellationToken.None);
            await CleanupEntryAsync(entryName, CancellationToken.None);
            return VpnConnectionResult.Failed(VpnLinkStatus.Error, "A VPN discou, mas o adaptador de rede não ficou ativo a tempo.");
        }

        _activeEntryName = entryName;
        return VpnConnectionResult.Ok(VpnLinkStatus.Connected);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var entryName = _activeEntryName;
        if (entryName is null)
        {
            return;
        }

        await rasDial.HangUpAsync(entryName, cancellationToken);
        await CleanupEntryAsync(entryName, cancellationToken);
        _activeEntryName = null;
    }

    public Task<VpnLinkStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_activeEntryName is null)
        {
            return Task.FromResult(VpnLinkStatus.Disconnected);
        }

        var status = adapterLocator.IsUp(_activeEntryName) ? VpnLinkStatus.Connected : VpnLinkStatus.Error;
        return Task.FromResult(status);
    }

    public Task<VpnAdapterInfo?> GetAdapterInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = _activeEntryName is null ? null : adapterLocator.ResolveAdapterInfo(_activeEntryName);
        return Task.FromResult(info);
    }

    public async Task ForceDisconnectByNameAsync(string entryName, CancellationToken cancellationToken = default)
    {
        await rasDial.HangUpAsync(entryName, cancellationToken);
        await CleanupEntryAsync(entryName, cancellationToken);
    }

    internal static string BuildServerAddress(VpnConnectionRequest request) => request.Endpoint.ToString();

    private async Task<bool> WaitUntilUpAsync(string entryName, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CONNECT_TIMEOUT);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                if (adapterLocator.IsUp(entryName))
                {
                    return true;
                }

                await Task.Delay(POLL_INTERVAL, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return false;
    }

    private async Task CleanupEntryAsync(string entryName, CancellationToken cancellationToken)
    {
        try
        {
            await scriptRunner.RunAsync(new Dictionary<string, string?> { ["Action"] = "Remove", ["Name"] = entryName }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao remover a entrada de VPN temporária '{Entry}'", entryName);
        }
    }
}
