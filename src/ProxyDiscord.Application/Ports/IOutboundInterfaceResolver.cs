using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IOutboundInterfaceResolver
{
    Task<OutboundInterfaceInfo?> CapturePhysicalInterfaceAsync(CancellationToken cancellationToken = default);
}
