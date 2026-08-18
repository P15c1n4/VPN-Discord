namespace ProxyDiscord.Application.Ports;

public interface ISystemClock
{
    DateTime UtcNow { get; }
}
