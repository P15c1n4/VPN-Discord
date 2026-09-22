using System.Net;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class VpnInterfaceGatewayResolver : IVpnInterfaceGatewayResolver
{
    public IPAddress? ResolveGateway(uint interfaceIndex) =>
        IpForwardNative.ReadIpv4Table()
            .Where(row => row.InterfaceIndex == interfaceIndex)
            .Select(row => row.NextHop.ToIpv4())
            .FirstOrDefault(nextHop =>
                nextHop.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !nextHop.Equals(IPAddress.Any));
}
