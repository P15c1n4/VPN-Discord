using System.Net;

namespace ProxyDiscord.Application.Ports;

public interface IVpnInterfaceGatewayResolver
{
    IPAddress? ResolveGateway(uint interfaceIndex);
}
