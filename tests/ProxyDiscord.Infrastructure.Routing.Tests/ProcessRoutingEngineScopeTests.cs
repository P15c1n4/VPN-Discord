using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing.Tests;

public class ProcessRoutingEngineScopeTests
{
    private const int TRACKED_PID = 4242;
    private static readonly VpnAdapterInfo VPN_ADAPTER = new("10.8.0.5", InterfaceIndex: 99, SubInterfaceIndex: 0);
    private static readonly TargetProcessSelector TARGET = new("Discord", @"C:\Discord\Discord.exe");

    private static PacketAddress Outbound() => PacketAddress.ForTest(outbound: true, 7);

    [Theory]
    [InlineData(TunnelProtocolScope.TcpAndUdp, "outbound and (tcp or udp)")]
    [InlineData(TunnelProtocolScope.TcpOnly, "outbound and tcp")]
    [InlineData(TunnelProtocolScope.UdpOnly, "outbound and udp")]
    public async Task CaptureFilter_IsNarrowedToTheScope(TunnelProtocolScope scope, string expected)
    {
        var harness = CreateEngine();

        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default, scope);
        await harness.Engine.StopAsync();

        Assert.Equal(expected, harness.Factory.LastNetworkFilter);
    }

    [Fact]
    public async Task TcpOnly_TunnelsTcpAndLeavesUdpOnTheDirectPath()
    {
        var harness = CreateEngine(trackedTcpPorts: [51000], trackedUdpPorts: [55000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default, TunnelProtocolScope.TcpOnly);

        var udp = TestPacketBuilder.BuildUdpPacket("192.168.1.50", 55000, "203.0.113.10", 50000);
        harness.Handle.Enqueue(TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443), Outbound());
        harness.Handle.Enqueue(udp, Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 2);
        await harness.Engine.StopAsync();

        var (_, _, tcpDstIp, tcpDstPort) = TestPacketBuilder.ReadAddressing(sent[0].Packet);
        Assert.Equal("192.168.1.50", tcpDstIp);
        Assert.Equal(harness.TcpRelay.ListenPort, tcpDstPort);

        Assert.Equal(udp, sent[1].Packet);
        Assert.True(sent[1].Address.Outbound);
    }

    [Fact]
    public async Task UdpOnly_TunnelsUdpAndLeavesTcpOnTheDirectPath()
    {
        var harness = CreateEngine(trackedTcpPorts: [51000], trackedUdpPorts: [55000]);
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default, TunnelProtocolScope.UdpOnly);

        var tcp = TestPacketBuilder.BuildTcpPacket("192.168.1.50", 51000, "203.0.113.10", 443);
        harness.Handle.Enqueue(TestPacketBuilder.BuildUdpPacket("192.168.1.50", 55000, "203.0.113.10", 50000), Outbound());
        harness.Handle.Enqueue(tcp, Outbound());

        var sent = await WaitForSentAsync(harness.Handle, count: 2);
        await harness.Engine.StopAsync();

        var (_, _, udpDstIp, udpDstPort) = TestPacketBuilder.ReadAddressing(sent[0].Packet);
        Assert.Equal("192.168.1.50", udpDstIp);
        Assert.Equal(harness.UdpRelay.ListenPort, udpDstPort);

        Assert.Equal(tcp, sent[1].Packet);
        Assert.True(sent[1].Address.Outbound);
    }

    [Fact]
    public async Task ExcludedTransport_DoesNotStartItsRelay()
    {
        var harness = CreateEngine();

        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default, TunnelProtocolScope.TcpOnly);
        var udpPort = harness.UdpRelay.ListenPort;
        var tcpPort = harness.TcpRelay.ListenPort;
        await harness.Engine.StopAsync();

        Assert.Equal(0, udpPort);
        Assert.NotEqual(0, tcpPort);
    }

    [Fact]
    public async Task Scope_IsReportedInTheDiagnostics()
    {
        var harness = CreateEngine();

        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default, TunnelProtocolScope.UdpOnly);
        var scope = harness.Diagnostics.Scope;
        await harness.Engine.StopAsync();

        Assert.Equal(TunnelProtocolScope.UdpOnly, scope);
    }

    // O handle NETWORK é o último passo do Start: quando ele falha, relay e watcher já subiram.
    [Fact]
    public async Task FailedStart_LeavesNoRelayListeningAndCanBeRetried()
    {
        var harness = CreateEngine();
        harness.Factory.NetworkOpenFailure = new InvalidOperationException("driver indisponível");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default, TunnelProtocolScope.TcpAndUdp));

        Assert.False(harness.Engine.IsRunning);
        var abandonedTcpPort = harness.TcpRelay.ListenPort;
        var abandonedUdpPort = harness.UdpRelay.ListenPort;
        Assert.False(IsListening(abandonedTcpPort), "o relay TCP continuou escutando depois do Start falhar");
        Assert.False(IsListening(abandonedUdpPort), "o relay UDP continuou escutando depois do Start falhar");
        harness.Watcher.Received().Dispose();

        harness.Factory.NetworkOpenFailure = null;
        await harness.Engine.StartAsync(TARGET, VPN_ADAPTER, TunnelDnsSettings.Default, TunnelProtocolScope.TcpAndUdp);

        Assert.True(harness.Engine.IsRunning);
        Assert.NotEqual(abandonedTcpPort, harness.TcpRelay.ListenPort);

        await harness.Engine.StopAsync();
        Assert.False(IsListening(harness.TcpRelay.ListenPort));
    }

    private static bool IsListening(int port)
    {
        if (port == 0)
        {
            return false;
        }

        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            probe.Connect(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private sealed record Harness(
        ProcessRoutingEngine Engine,
        FakeWinDivertHandle Handle,
        TcpTunnelRelay TcpRelay,
        UdpTunnelRelay UdpRelay,
        TunnelDiagnostics Diagnostics,
        FakeWinDivertHandleFactory Factory,
        IProcessGroupWatcher Watcher);

    private static Harness CreateEngine(int[]? trackedTcpPorts = null, int[]? trackedUdpPorts = null)
    {
        var snapshot = new Dictionary<(TransportProtocol, int), int>();
        foreach (var port in trackedTcpPorts ?? [])
        {
            snapshot[(TransportProtocol.Tcp, port)] = TRACKED_PID;
        }

        foreach (var port in trackedUdpPorts ?? [])
        {
            snapshot[(TransportProtocol.Udp, port)] = TRACKED_PID;
        }

        var ipHelperReader = Substitute.For<IIpHelperTableReader>();
        ipHelperReader.SnapshotOwnerPids().Returns(snapshot);

        var watcher = Substitute.For<IProcessGroupWatcher>();
        watcher.IsTracked(TRACKED_PID).Returns(true);

        var handle = new FakeWinDivertHandle();
        var factory = new FakeWinDivertHandleFactory(handle, new FakeWinDivertSocketEvents());

        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(_ => DateTime.UtcNow);

        var diagnostics = new TunnelDiagnostics();
        var flows = new FlowRegistry(ipHelperReader);
        var tcpRelay = new TcpTunnelRelay(flows, diagnostics, NullLogger<TcpTunnelRelay>.Instance);
        var udpRelay = new UdpTunnelRelay(flows, diagnostics, NullLogger<UdpTunnelRelay>.Instance);

        var engine = new ProcessRoutingEngine(
            factory, flows, watcher, tcpRelay, udpRelay, diagnostics, clock,
            NullLogger<ProcessRoutingEngine>.Instance);

        return new Harness(engine, handle, tcpRelay, udpRelay, diagnostics, factory, watcher);
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
