using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Tests;

public class DisconnectVpnUseCaseTests
{
    private readonly IProcessRoutingEngine _routingEngine = Substitute.For<IProcessRoutingEngine>();
    private readonly IVpnRouteManager _routeManager = Substitute.For<IVpnRouteManager>();
    private readonly IVpnConnection _vpnConnection = Substitute.For<IVpnConnection>();
    private readonly IConnectionStateStore _stateStore = Substitute.For<IConnectionStateStore>();
    private readonly RoutingSessionContext _sessionContext = new();

    private DisconnectVpnUseCase CreateUseCase() =>
        new(_routingEngine, _vpnConnection, _routeManager, _stateStore, _sessionContext,
            NullLogger<DisconnectVpnUseCase>.Instance);

    [Fact]
    public async Task ExecuteAsync_StopsRoutingBeforeHangingUpVpn()
    {
        var callOrder = new List<string>();
        _routingEngine.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("routing"));
        _vpnConnection.DisconnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("vpn"));
        _stateStore.ClearStateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("state"));

        await CreateUseCase().ExecuteAsync();

        Assert.Equal(["routing", "vpn", "state"], callOrder);
        Assert.Equal(ConnectionStatus.Idle, _sessionContext.Status);
    }

    [Fact]
    public async Task ExecuteAsync_RoutingEngineStopThrows_StillDisconnectsVpnAndClearsState()
    {
        _routingEngine.StopAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("já parado"));

        await CreateUseCase().ExecuteAsync();

        await _vpnConnection.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await _stateStore.Received(1).ClearStateAsync(Arg.Any<CancellationToken>());
        Assert.Equal(ConnectionStatus.Idle, _sessionContext.Status);
    }

    [Fact]
    public async Task ExecuteAsync_VpnDisconnectThrows_StillClearsStateAndGoesIdle()
    {
        _vpnConnection.DisconnectAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("já desconectado"));

        await CreateUseCase().ExecuteAsync();

        await _stateStore.Received(1).ClearStateAsync(Arg.Any<CancellationToken>());
        Assert.Equal(ConnectionStatus.Idle, _sessionContext.Status);
    }

    [Fact]
    public async Task ExecuteAsync_IsIdempotent_CalledTwiceInARow_DoesNotThrow()
    {
        var useCase = CreateUseCase();

        await useCase.ExecuteAsync();
        var secondCallException = await Record.ExceptionAsync(() => useCase.ExecuteAsync());

        Assert.Null(secondCallException);
        Assert.Equal(ConnectionStatus.Idle, _sessionContext.Status);
    }
}
