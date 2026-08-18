using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.Entities;

namespace ProxyDiscord.Application.UseCases;

public sealed class DiscoverRunningProcessesUseCase(IProcessRepository processRepository)
{
    public async Task<IReadOnlyList<ProcessInfo>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var processes = await processRepository.GetRunningProcessesAsync(cancellationToken);

        return processes
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
