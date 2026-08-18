using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IPingService
{
    IAsyncEnumerable<PingResult> PingManyAsync(
        IReadOnlyList<HostProbe> targets,
        TimeSpan perItemTimeout,
        CancellationToken cancellationToken = default);
}
