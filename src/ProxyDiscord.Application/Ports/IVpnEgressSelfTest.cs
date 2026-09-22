using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IVpnEgressSelfTest
{
    Task<EgressSelfTestResult> RunAsync(
        VpnAdapterInfo adapter,
        OutboundInterfaceInfo? directInterface = null,
        CancellationToken cancellationToken = default);
}
