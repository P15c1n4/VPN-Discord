using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Infrastructure.OpenVpn;

internal static class TunnelAdapterReadiness
{
    internal static bool HasUsableAddress(VpnAdapterInfo adapter)
    {
        return NetworkInterface.GetAllNetworkInterfaces().Any(network =>
        {
            if (network.OperationalStatus != OperationalStatus.Up) return false;
            var properties = network.GetIPProperties();
            return properties.GetIPv4Properties()?.Index == adapter.InterfaceIndex &&
                   properties.UnicastAddresses.Any(address => IsUsableAddress(
                       address.Address, address.DuplicateAddressDetectionState, adapter.LocalIp));
        });
    }

    internal static bool IsUsableAddress(IPAddress address, DuplicateAddressDetectionState state, string expectedIp) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        state == DuplicateAddressDetectionState.Preferred &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast) &&
        !IPAddress.IsLoopback(address) &&
        string.Equals(address.ToString(), expectedIp, StringComparison.Ordinal);

    internal static async Task WaitAsync(
        VpnAdapterInfo adapter, CancellationToken cancellationToken,
        Func<VpnAdapterInfo, bool>? isReady = null, TimeSpan? timeout = null)
    {
        isReady ??= HasUsableAddress;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try { if (isReady(adapter)) return; }
                catch (NetworkInformationException) { }
                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"O adaptador VPN {adapter.InterfaceIndex} não disponibilizou o IPv4 {adapter.LocalIp} no prazo de preparação.");
        }
    }
}
