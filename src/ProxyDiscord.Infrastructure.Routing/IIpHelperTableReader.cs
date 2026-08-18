using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public interface IIpHelperTableReader
{
    IReadOnlyDictionary<(TransportProtocol Protocol, int LocalPort), int> SnapshotOwnerPids();
}
