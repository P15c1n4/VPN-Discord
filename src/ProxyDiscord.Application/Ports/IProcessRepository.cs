using ProxyDiscord.Domain.Entities;

namespace ProxyDiscord.Application.Ports;

public interface IProcessRepository
{
    Task<IReadOnlyList<ProcessInfo>> GetRunningProcessesAsync(CancellationToken cancellationToken = default);
}
