using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Application.UseCases;

public sealed class TestServerLatenciesUseCase(IPingService pingService)
{
    private static readonly TimeSpan PER_SERVER_TIMEOUT = TimeSpan.FromSeconds(3);

    public IAsyncEnumerable<PingResult> ExecuteAsync(
        IReadOnlyList<VpnGateServerEntry> servers,
        CancellationToken cancellationToken = default)
    {
        var targets = servers
            .Select(server => new HostProbe(server.HostName, ResolveProbeTarget(server)))
            .ToList();

        return pingService.PingManyAsync(targets, PER_SERVER_TIMEOUT, cancellationToken);
    }

    private static string ResolveProbeTarget(VpnGateServerEntry server) =>
        string.IsNullOrWhiteSpace(server.IpAddress)
            ? server.EndpointFor(server.PreferredProtocol)?.Host ?? server.HostName
            : server.IpAddress;
}
