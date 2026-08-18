namespace ProxyDiscord.Domain.Entities;

public sealed record RoutingSession(
    ProcessInfo TargetProcess,
    string VpnAdapterLocalIp,
    uint VpnInterfaceIndex,
    DateTime StartedUtc);
