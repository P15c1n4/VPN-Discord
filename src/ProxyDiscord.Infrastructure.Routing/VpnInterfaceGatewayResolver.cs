using System.Net;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class VpnInterfaceGatewayResolver : IVpnInterfaceGatewayResolver
{
    public IPAddress? ResolveGateway(uint interfaceIndex)
    {
        var candidates = IpForwardNative.ReadIpv4Table()
            .Where(row => row.InterfaceIndex == interfaceIndex)
            .OrderByDescending(row => row.IsDefaultRoute)
            .ThenBy(row => row.Metric)
            .Select(row => row.NextHop.ToIpv4());

        return candidates.FirstOrDefault(IsUsableGateway);
    }

    private static bool IsUsableGateway(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.Broadcast) ||
            IPAddress.IsLoopback(address))
        {
            return false;
        }

        return address.GetAddressBytes()[0] < 224;
    }
}
