using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Ports;

public interface IProcessRoutingEngine : IAsyncDisposable
{
    Task StartAsync(
        TargetProcessSelector target,
        VpnAdapterInfo vpnAdapter,
        TunnelDnsSettings dnsSettings,
        TunnelProtocolScope scope = TunnelProtocolScope.TcpAndUdp,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    bool IsRunning { get; }

    event EventHandler? TrafficObserved;
}
