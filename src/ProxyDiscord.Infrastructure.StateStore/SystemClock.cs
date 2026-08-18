using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.StateStore;

public sealed class SystemClock : ISystemClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
