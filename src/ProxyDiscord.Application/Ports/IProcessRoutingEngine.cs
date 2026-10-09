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
        ProcessRoutingBackend backend = ProcessRoutingBackend.WinDivert,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    bool IsRunning { get; }

    event EventHandler? TrafficObserved;
    event EventHandler<RoutingEngineFailureEventArgs>? Failed;
}

public sealed class RoutingEngineFailureEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}
