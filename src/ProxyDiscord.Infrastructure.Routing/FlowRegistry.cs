using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed record TrackedFlow(
    TransportProtocol Protocol,
    int LocalPort,
    int ProcessId,
    string? RemoteIp,
    int RemotePort,
    DateTime CreatedUtc)
{
    public bool HasDestination => RemoteIp is not null && RemotePort > 0;
}

public sealed class FlowRegistry(IIpHelperTableReader tableReader)
{
    private static readonly TimeSpan MAX_ENTRY_AGE = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan FALLBACK_COOLDOWN = TimeSpan.FromMilliseconds(50);

    private readonly Dictionary<(TransportProtocol, int), TrackedFlow> _flows = new();
    private readonly object _lock = new();
    private DateTime _lastFallbackUtc = DateTime.MinValue;

    private readonly HashSet<(TransportProtocol, int)> _ipv6Ports = [];

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _flows.Count;
            }
        }
    }

    public int SeedFromIpHelper(DateTime nowUtc)
    {
        var snapshot = tableReader.SnapshotOwnerPids();
        lock (_lock)
        {
            foreach (var ((protocol, localPort), pid) in snapshot)
            {
                _flows[(protocol, localPort)] = new TrackedFlow(protocol, localPort, pid, null, 0, nowUtc);
            }

            return _flows.Count;
        }
    }

    public void Apply(SocketEvent socketEvent, DateTime nowUtc)
    {
        if (!TryMapProtocol(socketEvent.Protocol, out var protocol) || socketEvent.LocalPort == 0)
        {
            return;
        }

        var key = (protocol, (int)socketEvent.LocalPort);

        lock (_lock)
        {
            switch (socketEvent.Kind)
            {
                case SocketEventKind.Close:
                    _flows.Remove(key);
                    _ipv6Ports.Remove(key);
                    return;

                case SocketEventKind.Bind:
                case SocketEventKind.Connect:
                case SocketEventKind.Listen:
                case SocketEventKind.Accept:
                    if (socketEvent.IsIpv6 && socketEvent.LocalIpv4 is null)
                    {
                        _ipv6Ports.Add(key);
                    }

                    var remoteIp = socketEvent.RemoteIpv4;
                    var remotePort = socketEvent.RemotePort;

                    if (remoteIp is null && _flows.TryGetValue(key, out var existing) && existing.HasDestination)
                    {
                        remoteIp = existing.RemoteIp;
                        remotePort = (ushort)existing.RemotePort;
                    }

                    _flows[key] = new TrackedFlow(
                        protocol, key.Item2, socketEvent.ProcessId, remoteIp, remotePort, nowUtc);
                    return;

                default:
                    return;
            }
        }
    }

    public bool TryGetOwner(TransportProtocol protocol, int localPort, out int processId, out bool fromSocketLayer)
    {
        lock (_lock)
        {
            if (_flows.TryGetValue((protocol, localPort), out var flow))
            {
                processId = flow.ProcessId;
                fromSocketLayer = true;
                return true;
            }
        }

        if (!TryFallbackRefresh(protocol, localPort, out processId))
        {
            fromSocketLayer = false;
            return false;
        }

        fromSocketLayer = false;
        return true;
    }

    public bool TryGetDestination(TransportProtocol protocol, int localPort, out TrackedFlow flow)
    {
        lock (_lock)
        {
            return _flows.TryGetValue((protocol, localPort), out flow!) && flow.HasDestination;
        }
    }

    public void RecordDestinationFromPacket(
        TransportProtocol protocol, int localPort, int processId, string remoteIp, int remotePort, DateTime nowUtc)
    {
        lock (_lock)
        {
            _flows[(protocol, localPort)] = new TrackedFlow(
                protocol, localPort, processId, remoteIp, remotePort, nowUtc);
        }
    }

    public bool IsIpv6Port(TransportProtocol protocol, int localPort)
    {
        lock (_lock)
        {
            return _ipv6Ports.Contains((protocol, localPort));
        }
    }

    public int ExpireStale(DateTime nowUtc)
    {
        lock (_lock)
        {
            var expired = _flows
                .Where(kvp => nowUtc - kvp.Value.CreatedUtc >= MAX_ENTRY_AGE)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
            {
                _flows.Remove(key);
                _ipv6Ports.Remove(key);
            }

            return expired.Count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _flows.Clear();
            _ipv6Ports.Clear();
        }
    }

    private bool TryFallbackRefresh(TransportProtocol protocol, int localPort, out int processId)
    {
        processId = 0;

        lock (_lock)
        {
            if (DateTime.UtcNow - _lastFallbackUtc < FALLBACK_COOLDOWN)
            {
                return false;
            }

            _lastFallbackUtc = DateTime.UtcNow;
        }

        var snapshot = tableReader.SnapshotOwnerPids();
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            foreach (var ((snapshotProtocol, snapshotPort), pid) in snapshot)
            {
                if (!_flows.ContainsKey((snapshotProtocol, snapshotPort)))
                {
                    _flows[(snapshotProtocol, snapshotPort)] =
                        new TrackedFlow(snapshotProtocol, snapshotPort, pid, null, 0, now);
                }
            }

            if (!_flows.TryGetValue((protocol, localPort), out var flow))
            {
                return false;
            }

            processId = flow.ProcessId;
            return true;
        }
    }

    private static bool TryMapProtocol(byte ipProtocol, out TransportProtocol protocol)
    {
        switch (ipProtocol)
        {
            case (byte)TransportProtocol.Tcp:
                protocol = TransportProtocol.Tcp;
                return true;
            case (byte)TransportProtocol.Udp:
                protocol = TransportProtocol.Udp;
                return true;
            default:
                protocol = default;
                return false;
        }
    }
}
