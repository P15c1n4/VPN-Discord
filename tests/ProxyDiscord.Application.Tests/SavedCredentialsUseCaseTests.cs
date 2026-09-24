using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Application.Vpn;
using ProxyDiscord.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class SavedCredentialsUseCaseTests
{
    [Fact]
    public async Task FailedAuthentication_DoesNotSavePasswordOrUsername()
    {
        var fixture = new Fixture(new UserConfiguration(true, true));

        await fixture.UseCase.SaveAfterSuccessfulConnectionAsync(
            "OpenVpn:vpn.example:443", "alice", "wrong-password", connectionSucceeded: false);

        Assert.Equal(0, fixture.CredentialsStore.SaveCount);
    }

    [Fact]
    public async Task SuccessfulConnection_SavesManuallyEnteredPasswordWhenBothOptionsAreEnabled()
    {
        var fixture = new Fixture(new UserConfiguration(true, true));

        await fixture.UseCase.SaveAfterSuccessfulConnectionAsync(
            "OpenVpn:vpn.example:443", "alice", "valid-password", connectionSucceeded: true);

        Assert.Equal("valid-password", fixture.CredentialsStore.LastPassword);
        Assert.Equal(1, fixture.CredentialsStore.SaveCount);
    }

    [Fact]
    public async Task SuccessfulConnection_DoesNotSavePasswordWhenPasswordOptionIsDisabled()
    {
        var fixture = new Fixture(new UserConfiguration(true, false));

        await fixture.UseCase.SaveAfterSuccessfulConnectionAsync(
            "OpenVpn:vpn.example:443", "alice", "valid-password", connectionSucceeded: true);

        Assert.Null(fixture.CredentialsStore.LastPassword);
        Assert.Equal(1, fixture.CredentialsStore.SaveCount);
    }

    [Fact]
    public async Task PasswordOptionAlone_DoesNotSaveCredentialsWhenServerCredentialSavingIsDisabled()
    {
        var fixture = new Fixture(new UserConfiguration(false, true));

        await fixture.UseCase.SaveAfterSuccessfulConnectionAsync(
            "OpenVpn:vpn.example:443", "alice", "valid-password", connectionSucceeded: true);

        Assert.Equal(0, fixture.CredentialsStore.SaveCount);
    }

    [Fact]
    public async Task PasswordOptionDisabled_DoesNotRestorePreviouslyStoredPassword()
    {
        var fixture = new Fixture(new UserConfiguration(true, false));
        fixture.CredentialsStore.Stored = new ServerCredentials("alice", "old-password");

        var found = await fixture.UseCase.FindAsync("OpenVpn:vpn.example:443");

        Assert.Equal("alice", found?.Username);
        Assert.Null(found?.Password);
    }

    [Fact]
    public async Task DisconnectDuringDial_CancelsPendingAttemptAndPreventsSecondSession()
    {
        var provider = new BlockingVpnProvider();
        var router = new VpnConnectionRouter([provider], NullLogger<VpnConnectionRouter>.Instance);
        var request = new VpnConnectionRequest(
            HostEndpoint.Parse("vpn.example:443", 443),
            VpnProtocol.OpenVpn,
            "alice",
            "password",
            "test-entry",
            "profile");

        var firstAttempt = router.ConnectAsync(request);
        await provider.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var concurrentAttempt = await router.ConnectAsync(request);
        Assert.False(concurrentAttempt.Success);
        Assert.Equal(1, provider.ConnectCount);

        await router.DisconnectAsync();
        var firstResult = await firstAttempt;

        Assert.False(firstResult.Success);
        Assert.Equal(1, provider.DisconnectCount);
        Assert.Equal(VpnLinkStatus.Disconnected, await router.GetStatusAsync());
    }

    [Fact]
    public async Task RejectedAuthentication_ImmediatelyCleansPartialProviderSession()
    {
        var provider = new BlockingVpnProvider(rejectImmediately: true);
        var router = new VpnConnectionRouter([provider], NullLogger<VpnConnectionRouter>.Instance);
        var request = new VpnConnectionRequest(
            HostEndpoint.Parse("vpn.example:443", 443),
            VpnProtocol.OpenVpn,
            "alice",
            "wrong-password",
            "test-entry",
            "profile");

        var result = await router.ConnectAsync(request);

        Assert.False(result.Success);
        Assert.Equal(1, provider.ConnectCount);
        Assert.Equal(1, provider.DisconnectCount);
        Assert.Equal(VpnLinkStatus.Disconnected, await router.GetStatusAsync());
    }

    private sealed class Fixture
    {
        public Fixture(UserConfiguration configuration)
        {
            ConfigurationStore = new FakeConfigurationStore(configuration);
            CredentialsStore = new FakeCredentialsStore();
            UseCase = new SavedCredentialsUseCase(ConfigurationStore, CredentialsStore);
        }

        public FakeConfigurationStore ConfigurationStore { get; }
        public FakeCredentialsStore CredentialsStore { get; }
        public SavedCredentialsUseCase UseCase { get; }
    }

    private sealed class FakeConfigurationStore(UserConfiguration configuration) : IUserConfigurationStore
    {
        public Task<UserConfiguration> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task WriteAsync(UserConfiguration value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeCredentialsStore : IServerCredentialsStore
    {
        public ServerCredentials? Stored { get; set; }
        public string? LastPassword { get; private set; }
        public int SaveCount { get; private set; }

        public Task<ServerCredentials?> FindAsync(string serverKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored);

        public Task SaveAsync(
            string serverKey,
            string username,
            string? manuallyEnteredPassword,
            CancellationToken cancellationToken = default)
        {
            LastPassword = manuallyEnteredPassword;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingVpnProvider(bool rejectImmediately = false) : IVpnProvider
    {
        public event EventHandler<VpnConnectionLostEventArgs>? ConnectionLost
        {
            add { }
            remove { }
        }

        public VpnProtocol Protocol => VpnProtocol.OpenVpn;
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }

        public async Task<VpnConnectionResult> ConnectAsync(
            VpnConnectionRequest request,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            ConnectStarted.TrySetResult();
            if (rejectImmediately)
            {
                return VpnConnectionResult.Failed(VpnLinkStatus.Error, "Autenticação recusada.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return VpnConnectionResult.Ok(VpnLinkStatus.Connected);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            return Task.CompletedTask;
        }

        public Task<VpnLinkStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VpnLinkStatus.Disconnected);

        public Task<VpnAdapterInfo?> GetAdapterInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<VpnAdapterInfo?>(null);

        public Task ForceDisconnectByNameAsync(string entryName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
