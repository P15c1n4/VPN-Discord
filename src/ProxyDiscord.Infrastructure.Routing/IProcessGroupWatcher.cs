using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Infrastructure.Routing;

public interface IProcessGroupWatcher : IDisposable
{
    void Start(TargetProcessSelector target);

    bool IsTracked(int pid);

    int TrackedCount { get; }
}
