using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Domain.ValueObjects;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class ConnectVpnUseCaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealSessionFailureStillStopsRoutingAndDisconnectsTheVpn(bool vpnFailed)
    {
        var vpn = new FakeVpnConnection();
        var routing = new FakeRoutingEngine();
        var routes = new FakeRouteManager();
        var state = new FakeStateStore();
        var session = new RoutingSessionContext();
        session.SetConnected(null);
        var disconnect = new DisconnectVpnUseCase(routing, vpn, routes, state, session,
            NullLogger<DisconnectVpnUseCase>.Instance);
        using var supervisor = new VpnConnectionSupervisor(vpn, routing, disconnect, session,
            NullLogger<VpnConnectionSupervisor>.Instance);

        if (vpnFailed) vpn.LoseConnection("Falha real na VPN");
        else routing.Fail("Falha real no roteamento");

        Assert.Equal(ConnectionStatus.Error, session.Status);
        Assert.Contains("Falha real", session.LastError);
        Assert.Equal(1, routing.StopCount);
        Assert.Equal(1, vpn.DisconnectCount);
        Assert.Equal(1, routes.RemoveCount);
        Assert.Equal(1, state.ClearCount);
    }

    [Fact]
    public async Task UnexpectedPostConnectFailure_SetsErrorAndRollsBackInsteadOfStayingConnecting()
    {
        var vpn = new FakeVpnConnection();
        var routes = new FakeRouteManager();
        var session = new RoutingSessionContext();
        var useCase = new ConnectVpnUseCase(
            vpn,
            new FakeRoutingEngine(),
            routes,
            new ThrowingEgressSelfTest(),
            new FakeOutboundInterfaceResolver(),
            new FakeStateStore(),
            new FakeLivenessChecker(),
            new FakeClock(),
            session,
            NullLogger<ConnectVpnUseCase>.Instance);

        var result = await useCase.ExecuteAsync(new ConnectVpnCommand(
            new ProcessInfo(1234, "discord", null),
            "vpn.example:443",
            VpnProtocol.OpenVpn,
            "user",
            "secret",
            "profile"));

        Assert.False(result.Success);
        Assert.Equal(ConnectionStatus.Error, session.Status);
        Assert.Contains("falha inesperada no teste", session.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, vpn.DisconnectCount);
        Assert.Equal(1, routes.RemoveCount);
    }

    private sealed class FakeVpnConnection : IVpnConnection
    {
        public int DisconnectCount { get; private set; }
        public event EventHandler<VpnConnectionLostEventArgs>? ConnectionLost;
        public void LoseConnection(string reason) => ConnectionLost?.Invoke(this, new VpnConnectionLostEventArgs(reason));

        public Task<VpnConnectionResult> ConnectAsync(VpnConnectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(VpnConnectionResult.Ok(VpnLinkStatus.Connected));

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            return Task.CompletedTask;
        }

        public Task<VpnLinkStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VpnLinkStatus.Connected);

        public Task<VpnAdapterInfo?> GetAdapterInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<VpnAdapterInfo?>(new VpnAdapterInfo("10.8.0.2", 9, 0, "10.8.0.1"));

        public Task ForceDisconnectByNameAsync(string entryName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeRouteManager : IVpnRouteManager
    {
        public int RemoveCount { get; private set; }
        public bool HasRoute => false;
        public void EnsureTunnelDefaultRoute(VpnAdapterInfo adapter) { }
        public void RemoveTunnelDefaultRoute() => RemoveCount++;
        public int RemoveOrphanedRoutes() => 0;
    }

    private sealed class ThrowingEgressSelfTest : IVpnEgressSelfTest
    {
        public Task<EgressSelfTestResult> RunAsync(
            VpnAdapterInfo adapter,
            OutboundInterfaceInfo? directInterface = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Falha inesperada no teste de saída.");
    }

    private sealed class FakeOutboundInterfaceResolver : IOutboundInterfaceResolver
    {
        public Task<OutboundInterfaceInfo?> CapturePhysicalInterfaceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<OutboundInterfaceInfo?>(null);
    }

    private sealed class FakeRoutingEngine : IProcessRoutingEngine
    {
        public event EventHandler? TrafficObserved
        {
            add { }
            remove { }
        }
        public event EventHandler<RoutingEngineFailureEventArgs>? Failed;
        public void Fail(string reason) => Failed?.Invoke(this, new RoutingEngineFailureEventArgs(reason));
        public int StopCount { get; private set; }
        public bool IsRunning => false;
        public Task StartAsync(
            TargetProcessSelector target,
            VpnAdapterInfo vpnAdapter,
            TunnelDnsSettings dnsSettings,
            TunnelProtocolScope scope = TunnelProtocolScope.TcpAndUdp,
            ProcessRoutingBackend backend = ProcessRoutingBackend.WinDivert,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStateStore : IConnectionStateStore
    {
        public int ClearCount { get; private set; }
        public Task WriteActiveStateAsync(ConnectionStateRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearStateAsync(CancellationToken cancellationToken = default)
        {
            ClearCount++;
            return Task.CompletedTask;
        }
        public Task<ConnectionStateRecord?> TryReadStaleStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ConnectionStateRecord?>(null);
    }

    private sealed class FakeLivenessChecker : IProcessLivenessChecker
    {
        public (int Pid, DateTime StartedUtc) GetCurrentProcessInfo() => (1, DateTime.UtcNow);
        public bool IsSameProcessStillRunning(int pid, DateTime expectedStartUtc) => true;
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
