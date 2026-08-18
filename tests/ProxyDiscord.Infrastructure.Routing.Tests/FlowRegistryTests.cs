using NSubstitute;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing.Tests;

public class FlowRegistryTests
{
    private const int PID = 4242;
    private static readonly DateTime NOW = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static IIpHelperTableReader Reader(params (TransportProtocol Protocol, int Port, int Pid)[] entries)
    {
        var reader = Substitute.For<IIpHelperTableReader>();
        reader.SnapshotOwnerPids().Returns(
            entries.ToDictionary(e => (e.Protocol, e.Port), e => e.Pid));
        return reader;
    }

    private static SocketEvent Event(
        SocketEventKind kind,
        int localPort,
        TransportProtocol protocol = TransportProtocol.Tcp,
        string? remoteIp = "203.0.113.10",
        ushort remotePort = 443,
        bool isIpv6 = false) =>
        new(kind, PID, (byte)protocol, (ushort)localPort, remotePort, isIpv6 ? null : "192.168.1.50", remoteIp, isIpv6);

    [Fact]
    public void SeedFromIpHelper_MakesPreexistingConnectionsVisible()
    {
        var registry = new FlowRegistry(Reader((TransportProtocol.Tcp, 51000, PID)));

        var seeded = registry.SeedFromIpHelper(NOW);

        Assert.Equal(1, seeded);
        Assert.True(registry.TryGetOwner(TransportProtocol.Tcp, 51000, out var pid, out _));
        Assert.Equal(PID, pid);
    }

    [Fact]
    public void Connect_RecordsOwnerAndDestination()
    {
        var registry = new FlowRegistry(Reader());

        registry.Apply(Event(SocketEventKind.Connect, 51000), NOW);

        Assert.True(registry.TryGetOwner(TransportProtocol.Tcp, 51000, out var pid, out var fromSocketLayer));
        Assert.Equal(PID, pid);
        Assert.True(fromSocketLayer);
        Assert.True(registry.TryGetDestination(TransportProtocol.Tcp, 51000, out var flow));
        Assert.Equal("203.0.113.10", flow.RemoteIp);
        Assert.Equal(443, flow.RemotePort);
    }

    [Fact]
    public void Close_RemovesTheFlow()
    {
        var registry = new FlowRegistry(Reader());
        registry.Apply(Event(SocketEventKind.Connect, 51000), NOW);

        registry.Apply(Event(SocketEventKind.Close, 51000), NOW);

        Assert.False(registry.TryGetDestination(TransportProtocol.Tcp, 51000, out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void BindAfterConnect_DoesNotEraseTheKnownDestination()
    {
        var registry = new FlowRegistry(Reader());
        registry.Apply(Event(SocketEventKind.Connect, 51000), NOW);

        registry.Apply(Event(SocketEventKind.Bind, 51000, remoteIp: null, remotePort: 0), NOW);

        Assert.True(registry.TryGetDestination(TransportProtocol.Tcp, 51000, out var flow));
        Assert.Equal("203.0.113.10", flow.RemoteIp);
        Assert.Equal(443, flow.RemotePort);
    }

    [Fact]
    public void RecordDestinationFromPacket_FillsInWhatTheSocketLayerCouldNotKnow()
    {
        var registry = new FlowRegistry(Reader());
        registry.Apply(Event(SocketEventKind.Bind, 55000, TransportProtocol.Udp, remoteIp: null, remotePort: 0), NOW);

        Assert.False(registry.TryGetDestination(TransportProtocol.Udp, 55000, out _));

        registry.RecordDestinationFromPacket(TransportProtocol.Udp, 55000, PID, "203.0.113.77", 50000, NOW);

        Assert.True(registry.TryGetDestination(TransportProtocol.Udp, 55000, out var flow));
        Assert.Equal("203.0.113.77", flow.RemoteIp);
        Assert.Equal(50000, flow.RemotePort);
    }

    [Fact]
    public void Ipv6Socket_IsFlaggedSoItsPacketsCanBeBlocked()
    {
        var registry = new FlowRegistry(Reader());

        registry.Apply(Event(SocketEventKind.Connect, 51900, remoteIp: null, isIpv6: true), NOW);

        Assert.True(registry.IsIpv6Port(TransportProtocol.Tcp, 51900));
        Assert.False(registry.IsIpv6Port(TransportProtocol.Tcp, 51000));
    }

    [Fact]
    public void TryGetOwner_OnAMiss_FallsBackToTheIpHelperTables()
    {
        var registry = new FlowRegistry(Reader((TransportProtocol.Tcp, 51000, PID)));

        var found = registry.TryGetOwner(TransportProtocol.Tcp, 51000, out var pid, out var fromSocketLayer);

        Assert.True(found);
        Assert.Equal(PID, pid);
        Assert.False(fromSocketLayer);
    }

    [Fact]
    public void TryGetOwner_ForAPortNobodyOwns_ReturnsFalse()
    {
        var registry = new FlowRegistry(Reader());

        Assert.False(registry.TryGetOwner(TransportProtocol.Tcp, 12345, out _, out _));
    }

    [Fact]
    public void ExpireStale_DropsEntriesThatNeverSawAClose()
    {
        var registry = new FlowRegistry(Reader());
        registry.Apply(Event(SocketEventKind.Connect, 51000), NOW);

        Assert.Equal(0, registry.ExpireStale(NOW.AddMinutes(5)));
        Assert.Equal(1, registry.ExpireStale(NOW.AddMinutes(31)));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Apply_IgnoresProtocolsTheTunnelCannotCarry()
    {
        var registry = new FlowRegistry(Reader());

        registry.Apply(new SocketEvent(
            SocketEventKind.Connect, PID, Protocol: 1, LocalPort: 51000, RemotePort: 0,
            LocalIpv4: "192.168.1.50", RemoteIpv4: null, IsIpv6: false), NOW);

        Assert.Equal(0, registry.Count);
    }
}
