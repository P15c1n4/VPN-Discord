using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Tests;

public class ConnectVpnUseCaseTests
{
    private static readonly ProcessInfo TARGET_PROCESS = new(1234, "Discord", @"C:\Discord\Discord.exe");
    private static readonly VpnAdapterInfo ADAPTER = new("10.8.0.5", 99, 0);

    private readonly IVpnConnection _vpnConnection = Substitute.For<IVpnConnection>();
    private readonly IProcessRoutingEngine _routingEngine = Substitute.For<IProcessRoutingEngine>();
    private readonly IVpnRouteManager _routeManager = Substitute.For<IVpnRouteManager>();
    private readonly IVpnEgressSelfTest _egressSelfTest = Substitute.For<IVpnEgressSelfTest>();
    private readonly IConnectionStateStore _stateStore = Substitute.For<IConnectionStateStore>();
    private readonly IProcessLivenessChecker _livenessChecker = Substitute.For<IProcessLivenessChecker>();
    private readonly ISystemClock _clock = Substitute.For<ISystemClock>();
    private readonly RoutingSessionContext _sessionContext = new();

    private ConnectVpnUseCase CreateUseCase() => new(
        _vpnConnection, _routingEngine, _routeManager, _egressSelfTest, _stateStore, _livenessChecker,
        _clock, _sessionContext, NullLogger<ConnectVpnUseCase>.Instance,
        trafficObservationTimeout: TimeSpan.FromMilliseconds(50));

    private ConnectVpnCommand ValidCommand() =>
        new(TARGET_PROCESS, "vpn.example.com", VpnProtocol.Sstp, "vpn", "vpn");

    public ConnectVpnUseCaseTests()
    {
        _livenessChecker.GetCurrentProcessInfo().Returns((Environment.ProcessId, DateTime.UtcNow));
        _clock.UtcNow.Returns(DateTime.UtcNow);

        _egressSelfTest.RunAsync(Arg.Any<VpnAdapterInfo>(), Arg.Any<CancellationToken>())
            .Returns(new Diagnostics.EgressSelfTestResult(true, "ok", "203.0.113.9", "198.51.100.7", true));
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ConnectsRoutesAndPersistsState_StatusBecomesConnected()
    {
        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(ADAPTER);

        var result = await CreateUseCase().ExecuteAsync(ValidCommand());

        Assert.True(result.Success);
        Assert.Equal(ConnectionStatus.Connected, _sessionContext.Status);
        await _routingEngine.Received(1).StartAsync(Arg.Is<TargetProcessSelector>(t => t.ProcessName == TARGET_PROCESS.Name), ADAPTER, Arg.Any<TunnelDnsSettings>(), Arg.Any<TunnelProtocolScope>(), Arg.Any<CancellationToken>());
        await _stateStore.Received(1).WriteActiveStateAsync(Arg.Any<ConnectionStateRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_InvalidAddress_FailsBeforeTouchingVpnConnection()
    {
        var command = ValidCommand() with { ServerAddressRaw = "" };

        var result = await CreateUseCase().ExecuteAsync(command);

        Assert.False(result.Success);
        Assert.Equal(ConnectionStatus.Error, _sessionContext.Status);
        await _vpnConnection.DidNotReceive().ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_VpnConnectFails_DoesNotStartRoutingEngine()
    {
        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Failed(VpnLinkStatus.Error, "credenciais inválidas"));

        var result = await CreateUseCase().ExecuteAsync(ValidCommand());

        Assert.False(result.Success);
        Assert.Equal(ConnectionStatus.Error, _sessionContext.Status);
        Assert.Equal("credenciais inválidas", _sessionContext.LastError);
        await _routingEngine.DidNotReceive().StartAsync(Arg.Any<TargetProcessSelector>(), Arg.Any<VpnAdapterInfo>(), Arg.Any<TunnelDnsSettings>(), Arg.Any<TunnelProtocolScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AdapterNotFound_RollsBackVpnConnection()
    {
        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns((VpnAdapterInfo?)null);

        var result = await CreateUseCase().ExecuteAsync(ValidCommand());

        Assert.False(result.Success);
        await _vpnConnection.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await _routingEngine.DidNotReceive().StartAsync(Arg.Any<TargetProcessSelector>(), Arg.Any<VpnAdapterInfo>(), Arg.Any<TunnelDnsSettings>(), Arg.Any<TunnelProtocolScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RoutingEngineThrows_RollsBackVpnConnection_AndDoesNotPersistState()
    {
        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(ADAPTER);
        _routingEngine.StartAsync(Arg.Any<TargetProcessSelector>(), Arg.Any<VpnAdapterInfo>(), Arg.Any<TunnelDnsSettings>(), Arg.Any<TunnelProtocolScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("driver ausente"));

        var result = await CreateUseCase().ExecuteAsync(ValidCommand());

        Assert.False(result.Success);
        Assert.Equal(ConnectionStatus.Error, _sessionContext.Status);
        await _vpnConnection.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await _stateStore.DidNotReceive().WriteActiveStateAsync(Arg.Any<ConnectionStateRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SetsConnectingStatus_BeforeAttemptingToDial()
    {
        var statusesObserved = new List<ConnectionStatus>();
        _sessionContext.PropertyChanged += (_, _) => statusesObserved.Add(_sessionContext.Status);
        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(ADAPTER);

        await CreateUseCase().ExecuteAsync(ValidCommand());

        Assert.Contains(ConnectionStatus.Connecting, statusesObserved);
        Assert.True(statusesObserved.IndexOf(ConnectionStatus.Connecting) < statusesObserved.IndexOf(ConnectionStatus.Connected));
    }
}
