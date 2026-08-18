using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.UseCases;

namespace ProxyDiscord.Application.Tests;

public class CleanupStaleStateOnStartupUseCaseTests
{
    private readonly IConnectionStateStore _stateStore = Substitute.For<IConnectionStateStore>();
    private readonly IVpnRouteManager _routeManager = Substitute.For<IVpnRouteManager>();
    private readonly IVpnConnection _vpnConnection = Substitute.For<IVpnConnection>();
    private readonly IProcessLivenessChecker _livenessChecker = Substitute.For<IProcessLivenessChecker>();

    private CleanupStaleStateOnStartupUseCase CreateUseCase() =>
        new(_stateStore, _vpnConnection, _routeManager, _livenessChecker,
            NullLogger<CleanupStaleStateOnStartupUseCase>.Instance);

    private static readonly ConnectionStateRecord STALE_RECORD = new(
        OwnerPid: 4321, OwnerStartedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        TargetProcessId: 1234, TargetProcessName: "Discord", RasEntryName: "ProxyDiscord-Discord-abc123",
        CreatedUtc: new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));

    [Fact]
    public async Task ExecuteAsync_NoStateFile_DoesNothing()
    {
        _stateStore.TryReadStaleStateAsync(Arg.Any<CancellationToken>()).Returns((ConnectionStateRecord?)null);

        await CreateUseCase().ExecuteAsync();

        await _vpnConnection.DidNotReceive().ForceDisconnectByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _stateStore.DidNotReceive().ClearStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_OwnerProcessStillAlive_LeavesStateUntouched()
    {
        _stateStore.TryReadStaleStateAsync(Arg.Any<CancellationToken>()).Returns(STALE_RECORD);
        _livenessChecker.IsSameProcessStillRunning(STALE_RECORD.OwnerPid, STALE_RECORD.OwnerStartedUtc).Returns(true);

        await CreateUseCase().ExecuteAsync();

        await _vpnConnection.DidNotReceive().ForceDisconnectByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _stateStore.DidNotReceive().ClearStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_OwnerProcessGone_ForceDisconnectsByNameAndClearsState()
    {
        _stateStore.TryReadStaleStateAsync(Arg.Any<CancellationToken>()).Returns(STALE_RECORD);
        _livenessChecker.IsSameProcessStillRunning(STALE_RECORD.OwnerPid, STALE_RECORD.OwnerStartedUtc).Returns(false);

        await CreateUseCase().ExecuteAsync();

        await _vpnConnection.Received(1).ForceDisconnectByNameAsync(STALE_RECORD.RasEntryName, Arg.Any<CancellationToken>());
        await _stateStore.Received(1).ClearStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ForceDisconnectThrows_StillClearsState()
    {
        _stateStore.TryReadStaleStateAsync(Arg.Any<CancellationToken>()).Returns(STALE_RECORD);
        _livenessChecker.IsSameProcessStillRunning(STALE_RECORD.OwnerPid, STALE_RECORD.OwnerStartedUtc).Returns(false);
        _vpnConnection.ForceDisconnectByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("sem conexão para desfazer")));

        var exception = await Record.ExceptionAsync(() => CreateUseCase().ExecuteAsync());

        Assert.Null(exception);
        await _stateStore.Received(1).ClearStateAsync(Arg.Any<CancellationToken>());
    }
}
