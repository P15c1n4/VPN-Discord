using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Ports;

public interface IVpnProvider : IVpnConnection
{
    VpnProtocol Protocol { get; }
}
