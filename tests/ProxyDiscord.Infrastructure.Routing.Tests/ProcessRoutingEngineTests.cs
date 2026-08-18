using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing.Tests;

public class ProcessRoutingEngineTests
{
    private const int TRACKED_PID = 4242;
    private const int OTHER_PID = 9999;
    private static readonly VpnAdapterInfo VPN_ADAPTER = new("10.8.0.5", InterfaceIndex: 99, SubInterfaceIndex: 0);
    private static readonly TargetProcessSelector TARGET = new("Discord", @"C:\Discord\Discord.exe");

    private static PacketAddress Outbound(uint ifIdx = 7) => PacketAddress.ForTest(outbound: true, ifIdx);

    [Fact]
    public async Task OutboundPacket_FromTrackedApp_IsRedirectedToTheLocalRelay()
    {
        var harness = CreateEngine(trackedPorts: [51000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.Handle.Enqueue(
            TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443), Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 1);
        await harness.Engine.StopAsync();

        var (srcIp, srcPort, dstIp, dstPort) = TestPacketBuilder.ReadAddressing(sent[0].Packet);
        Assert.Equal("192.168.1.50", dstIp);
        Assert.NotEqual("127.0.0.1", dstIp);
        Assert.Equal(harness.TcpRelay.ListenPort, dstPort);
        Assert.Equal("192.168.1.50", srcIp);
        Assert.Equal(51000, srcPort);
        Assert.False(sent[0].Address.Outbound);
    }

    [Fact]
    public async Task OutboundPacket_FromTrackedApp_RecordsTheRealDestination()
    {
        var harness = CreateEngine(trackedPorts: [51000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.Handle.Enqueue(
            TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443), Outbound());

        await WaitForSentAsync(harness.Handle, count: 1);

        Assert.True(harness.Flows.TryGetDestination(TransportProtocol.Tcp, 51000, out var flow));
        Assert.Equal("203.0.113.10", flow.RemoteIp);
        Assert.Equal(443, flow.RemotePort);

        await harness.Engine.StopAsync();
    }

    [Fact]
    public async Task ConnectionAnnouncedOnlyBySocketLayer_IsRedirectedFromItsVeryFirstPacket()
    {
        var harness = CreateEngine();
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.SocketEvents.Enqueue(new SocketEvent(
            SocketEventKind.Connect, TRACKED_PID, (byte)TransportProtocol.Tcp,
            LocalPort: 51500, RemotePort: 443,
            LocalIpv4: "192.168.1.50", RemoteIpv4: "203.0.113.10", IsIpv6: false));

        await WaitForFlowAsync(harness.Flows, TransportProtocol.Tcp, 51500);

        harness.Handle.Enqueue(
            TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51500, "203.0.113.10", 443), Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 1);
        await harness.Engine.StopAsync();

        var (_, _, _, dstPort) = TestPacketBuilder.ReadAddressing(sent[0].Packet);
        Assert.Equal(harness.TcpRelay.ListenPort, dstPort);
    }

    [Fact]
    public async Task SocketCloseEvent_RemovesTheFlow_SoARecycledPortCannotInheritIt()
    {
        var harness = CreateEngine();
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.SocketEvents.Enqueue(new SocketEvent(
            SocketEventKind.Connect, TRACKED_PID, (byte)TransportProtocol.Tcp, 51500, 443,
            "192.168.1.50", "203.0.113.10", false));
        await WaitForFlowAsync(harness.Flows, TransportProtocol.Tcp, 51500);

        harness.SocketEvents.Enqueue(new SocketEvent(
            SocketEventKind.Close, TRACKED_PID, (byte)TransportProtocol.Tcp, 51500, 443,
            "192.168.1.50", "203.0.113.10", false));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (harness.Flows.TryGetDestination(TransportProtocol.Tcp, 51500, out _) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await harness.Engine.StopAsync();

        Assert.False(harness.Flows.TryGetDestination(TransportProtocol.Tcp, 51500, out _));
    }

    [Fact]
    public async Task OutboundPacket_FromUntrackedProcess_PassesThroughUntouched()
    {
        var harness = CreateEngine(trackedPorts: [51000], untrackedPorts: [52000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        var original = TestPacketBuilder.BuildTcpPacket("192.168.1.50", 52000, "203.0.113.20", 80);
        harness.Handle.Enqueue(original, Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 1);
        await harness.Engine.StopAsync();

        Assert.Equal(original, sent[0].Packet);
        Assert.True(sent[0].Address.Outbound);
    }

    [Fact]
    public async Task MixedTraffic_OnlyTheTrackedAppIsRedirected()
    {
        var harness = CreateEngine(trackedPorts: [51000], untrackedPorts: [52000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.Handle.Enqueue(TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443), Outbound());
        harness.Handle.Enqueue(TestPacketBuilder.BuildTcpPacket("192.168.1.50", 52000, "203.0.113.20", 80), Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 2);
        await harness.Engine.StopAsync();

        var tracked = sent.Single(s => TestPacketBuilder.ReadAddressing(s.Packet).SrcPort == 51000);
        var untracked = sent.Single(s => TestPacketBuilder.ReadAddressing(s.Packet).SrcPort == 52000);

        Assert.Equal(harness.TcpRelay.ListenPort, TestPacketBuilder.ReadAddressing(tracked.Packet).DstPort);
        Assert.Equal("203.0.113.20", TestPacketBuilder.ReadAddressing(untracked.Packet).DstIp);
        Assert.Equal(80, TestPacketBuilder.ReadAddressing(untracked.Packet).DstPort);
    }

    [Fact]
    public async Task ReplyFromRelay_IsRestoredToLookLikeTheRealServer()
    {
        var harness = CreateEngine(trackedPorts: [51000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.Handle.Enqueue(TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443), Outbound());
        await WaitForSentAsync(harness.Handle, count: 1);

        harness.Handle.Enqueue(
            TestPacketBuilder.BuildTcpPacket("192.168.1.50", harness.TcpRelay.ListenPort, "192.168.1.50", 51000),
            Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 2);
        await harness.Engine.StopAsync();

        var (srcIp, srcPort, dstIp, dstPort) = TestPacketBuilder.ReadAddressing(sent[1].Packet);
        Assert.Equal("203.0.113.10", srcIp);
        Assert.Equal(443, srcPort);
        Assert.Equal("192.168.1.50", dstIp);
        Assert.Equal(51000, dstPort);
        Assert.True(sent[1].Address.Outbound);
    }

    [Fact]
    public async Task TrafficObserved_IsNotRaisedByRedirectionAlone()
    {
        var harness = CreateEngine(trackedPorts: [51000]);
        var raised = false;
        harness.Engine.TrafficObserved += (_, _) => raised = true;
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.Handle.Enqueue(TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443), Outbound());
        await WaitForSentAsync(harness.Handle, count: 1);
        await harness.Engine.StopAsync();

        Assert.False(raised);
    }

    [Fact]
    public async Task OutboundUdpPacket_FromTrackedApp_IsRedirectedToTheUdpRelay()
    {
        var harness = CreateEngine(trackedUdpPorts: [55000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.Handle.Enqueue(
            TestPacketBuilder.BuildUdpPacket("192.168.1.50", 55000, "203.0.113.10", 50000), Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 1);

        var (_, srcPort, dstIp, dstPort) = TestPacketBuilder.ReadAddressing(sent[0].Packet);
        Assert.Equal("192.168.1.50", dstIp);
        Assert.Equal(harness.UdpRelay.ListenPort, dstPort);
        Assert.Equal(55000, srcPort);
        Assert.False(sent[0].Address.Outbound);
        Assert.True(harness.Flows.TryGetDestination(TransportProtocol.Udp, 55000, out var flow));
        Assert.Equal("203.0.113.10", flow.RemoteIp);

        await harness.Engine.StopAsync();
    }

    [Fact]
    public async Task UnconnectedUdp_TakesItsDestinationFromThePacketItself()
    {
        var harness = CreateEngine();
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.SocketEvents.Enqueue(new SocketEvent(
            SocketEventKind.Bind, TRACKED_PID, (byte)TransportProtocol.Udp,
            LocalPort: 55500, RemotePort: 0, LocalIpv4: "192.168.1.50", RemoteIpv4: null, IsIpv6: false));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (harness.Flows.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        harness.Handle.Enqueue(
            TestPacketBuilder.BuildUdpPacket("192.168.1.50", 55500, "203.0.113.77", 50000), Outbound());

        await WaitForSentAsync(harness.Handle, count: 1);

        Assert.True(harness.Flows.TryGetDestination(TransportProtocol.Udp, 55500, out var flow));
        Assert.Equal("203.0.113.77", flow.RemoteIp);
        Assert.Equal(50000, flow.RemotePort);

        await harness.Engine.StopAsync();
    }

    [Fact]
    public async Task DnsQuery_FromTrackedApp_IsRedirectedRatherThanLeaking()
    {
        var harness = CreateEngine(trackedUdpPorts: [55001]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.Handle.Enqueue(
            TestPacketBuilder.BuildUdpPacket("192.168.1.50", 55001, "192.168.1.1", 53), Outbound());

        await WaitForSentAsync(harness.Handle, count: 1);

        Assert.True(harness.Flows.TryGetDestination(TransportProtocol.Udp, 55001, out var flow));
        Assert.Equal(53, flow.RemotePort);

        await harness.Engine.StopAsync();
    }

    [Fact]
    public async Task UdpFromUntrackedProcess_PassesThroughUntouched()
    {
        var harness = CreateEngine(trackedUdpPorts: [55000], untrackedUdpPorts: [56000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        var original = TestPacketBuilder.BuildUdpPacket("192.168.1.50", 56000, "203.0.113.20", 50000);
        harness.Handle.Enqueue(original, Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 1);
        await harness.Engine.StopAsync();

        Assert.Equal(original, sent[0].Packet);
    }

    [Fact]
    public async Task Ipv6FromTrackedApp_IsDroppedInsteadOfLeaking()
    {
        var harness = CreateEngine();
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.SocketEvents.Enqueue(new SocketEvent(
            SocketEventKind.Connect, TRACKED_PID, (byte)TransportProtocol.Tcp,
            LocalPort: 51900, RemotePort: 443, LocalIpv4: null, RemoteIpv4: null, IsIpv6: true));
        await WaitForFlowCountAsync(harness.Flows, 1);

        harness.Handle.Enqueue(TestPacketBuilder.BuildIpv6TcpPacket(51900, 443), PacketAddress.ForTest(true, isIpv6: true));

        harness.Handle.Enqueue(
            TestPacketBuilder.BuildTcpPacket("192.168.1.50", 52000, "203.0.113.20", 80), Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 1);
        await harness.Engine.StopAsync();

        Assert.Single(sent);
        Assert.Equal(52000, TestPacketBuilder.ReadAddressing(sent[0].Packet).SrcPort);
        Assert.Equal(1, harness.Diagnostics.Ipv6Dropped);
    }

    [Fact]
    public async Task RejectedInjection_IsCounted_RatherThanSilentlyIgnored()
    {
        var harness = CreateEngine(trackedPorts: [51000]);
        harness.Handle.SendFailureWin32Error = 87;
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        harness.Handle.Enqueue(TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443), Outbound());

        await WaitForSentAsync(harness.Handle, count: 1);
        await harness.Engine.StopAsync();

        Assert.Equal(1, harness.Diagnostics.InjectFailed);
        Assert.Equal(87, harness.Diagnostics.LastInjectError);
    }

    [Fact]
    public async Task StopAsync_UnblocksCaptureLoopAndReleasesResources()
    {
        var harness = CreateEngine();
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default);

        var stopTask = harness.Engine.StopAsync();
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(stopTask, completed);
        Assert.False(harness.Engine.IsRunning);
    }

    private sealed record Harness(
        ProcessRoutingEngine Engine,
        FakeWinDivertHandle Handle,
        FakeWinDivertSocketEvents SocketEvents,
        FlowRegistry Flows,
        TcpTunnelRelay TcpRelay,
        UdpTunnelRelay UdpRelay,
        TunnelDiagnostics Diagnostics,
        FakeWinDivertHandleFactory Factory);

    private static Harness CreateEngine(
        int[]? trackedPorts = null,
        int[]? untrackedPorts = null,
        int[]? trackedUdpPorts = null,
        int[]? untrackedUdpPorts = null)
    {
        var snapshot = new Dictionary<(TransportProtocol, int), int>();
        foreach (var port in trackedPorts ?? [])
        {
            snapshot[(TransportProtocol.Tcp, port)] = TRACKED_PID;
        }

        foreach (var port in untrackedPorts ?? [])
        {
            snapshot[(TransportProtocol.Tcp, port)] = OTHER_PID;
        }

        foreach (var port in trackedUdpPorts ?? [])
        {
            snapshot[(TransportProtocol.Udp, port)] = TRACKED_PID;
        }

        foreach (var port in untrackedUdpPorts ?? [])
        {
            snapshot[(TransportProtocol.Udp, port)] = OTHER_PID;
        }

        var ipHelperReader = Substitute.For<IIpHelperTableReader>();
        ipHelperReader.SnapshotOwnerPids().Returns(snapshot);

        var watcher = Substitute.For<IProcessGroupWatcher>();
        watcher.IsTracked(TRACKED_PID).Returns(true);
        watcher.IsTracked(OTHER_PID).Returns(false);

        var handle = new FakeWinDivertHandle();
        var socketEvents = new FakeWinDivertSocketEvents();
        var factory = new FakeWinDivertHandleFactory(handle, socketEvents);

        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(_ => DateTime.UtcNow);

        var diagnostics = new TunnelDiagnostics();
        var flows = new FlowRegistry(ipHelperReader);
        var tcpRelay = new TcpTunnelRelay(flows, diagnostics, NullLogger<TcpTunnelRelay>.Instance);
        var udpRelay = new UdpTunnelRelay(flows, diagnostics, NullLogger<UdpTunnelRelay>.Instance);

        var engine = new ProcessRoutingEngine(
            factory, flows, watcher, tcpRelay, udpRelay, diagnostics, clock,
            NullLogger<ProcessRoutingEngine>.Instance);

        return new Harness(engine, handle, socketEvents, flows, tcpRelay, udpRelay, diagnostics, factory);
    }

    private static async Task WaitForFlowAsync(FlowRegistry flows, TransportProtocol protocol, int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!flows.TryGetDestination(protocol, port, out _) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(flows.TryGetDestination(protocol, port, out _), $"O evento de socket para a porta {port} não foi processado.");
    }

    private static async Task WaitForFlowCountAsync(FlowRegistry flows, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (flows.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(flows.Count >= count, $"Esperava {count} fluxo(s) registrado(s), obteve {flows.Count}.");
    }

    private static async Task<List<(byte[] Packet, PacketAddress Address)>> WaitForSentAsync(
        FakeWinDivertHandle handle, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handle.SentPackets.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(
            handle.SentPackets.Count >= count,
            $"Esperava {count} pacote(s) enviado(s), obteve {handle.SentPackets.Count}.");
        return handle.SentPackets.Take(count).ToList();
    }
}
