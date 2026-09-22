using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class OutboundInterfaceResolver(ILogger<OutboundInterfaceResolver> logger)
    : IOutboundInterfaceResolver
{
    public Task<OutboundInterfaceInfo?> CapturePhysicalInterfaceAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface =>
                    networkInterface.OperationalStatus == OperationalStatus.Up &&
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Ethernet or
                        NetworkInterfaceType.Wireless80211)
                .Select(networkInterface => new
                {
                    Interface = networkInterface,
                    Properties = TryGetIpv4Properties(networkInterface),
                })
                .Where(candidate => candidate.Properties is not null)
                .ToList();

            var defaultRoutes = IpForwardNative.ReadIpv4Table()
                .Where(row => row.IsDefaultRoute)
                .OrderBy(row => row.Metric)
                .ToList();

            foreach (var route in defaultRoutes)
            {
                var networkInterface = interfaces
                    .FirstOrDefault(candidate => (uint)candidate.Properties!.Index == route.InterfaceIndex)
                    ?.Interface;

                if (networkInterface is null)
                {
                    continue;
                }

                var localIp = networkInterface.GetIPProperties().UnicastAddresses
                    .Select(address => address.Address)
                    .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (localIp is null)
                {
                    continue;
                }

                var result = new OutboundInterfaceInfo(
                    localIp.ToString(), route.InterfaceIndex, networkInterface.Name);

                logger.LogDebug(
                    "Interface física capturada antes da rota VPN: {Alias} if {IfIdx}, IP {Ip}, métrica {Metric}",
                    result.Alias, result.InterfaceIndex, result.LocalIp, route.Metric);
                return Task.FromResult<OutboundInterfaceInfo?>(result);
            }

            logger.LogWarning("Não foi possível identificar uma interface física IPv4 para o teste direto.");
            return Task.FromResult<OutboundInterfaceInfo?>(null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao capturar a interface física; o teste direto será ignorado.");
            return Task.FromResult<OutboundInterfaceInfo?>(null);
        }
    }

    private static IPv4InterfaceProperties? TryGetIpv4Properties(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties().GetIPv4Properties();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
