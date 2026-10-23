using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class ConnectionMisuseTests
{
    [Theory]
    [InlineData("physical", VpnProtocol.OpenVpn)]
    [InlineData("dial", VpnProtocol.OpenVpn)]
    [InlineData("adapter", VpnProtocol.OpenVpn)]
    [InlineData("egress", VpnProtocol.OpenVpn)]
    [InlineData("routing", VpnProtocol.OpenVpn)]
    [InlineData("state", VpnProtocol.OpenVpn)]
    [InlineData("physical", VpnProtocol.Sstp)]
    [InlineData("dial", VpnProtocol.Sstp)]
    [InlineData("adapter", VpnProtocol.Sstp)]
    [InlineData("egress", VpnProtocol.Sstp)]
    [InlineData("routing", VpnProtocol.Sstp)]
    [InlineData("state", VpnProtocol.Sstp)]
    public async Task CancelAtEachStageLeavesNoActiveSessionAndAllowsRetry(string stage, VpnProtocol protocol)
    {
        using var env = new MisuseTestEnvironment();
        var provider = protocol == VpnProtocol.OpenVpn ? env.OpenVpn : env.Sstp;
        var pause = new MisuseTestEnvironment.PausePoint();
        switch (stage)
        {
            case "physical": env.Stages.PhysicalPause = pause; break;
            case "dial": provider.DialPause = pause; break;
            case "adapter": provider.AdapterPause = pause; break;
            case "egress": env.Stages.EgressPause = pause; break;
            case "routing": env.Routing.StartPause = pause; break;
            case "state": env.Stages.StatePause = pause; break;
        }
        var attempt = env.Connect.ExecuteAsync(MisuseTestEnvironment.Command(protocol));
        await pause.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var duplicate = await env.Connect.ExecuteAsync(MisuseTestEnvironment.Command(protocol));
        Assert.False(duplicate.Success);
        await Task.WhenAll(env.Disconnect.ExecuteAsync(), env.Disconnect.ExecuteAsync()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False((await attempt).Success);
        Assert.Equal(ConnectionStatus.Idle, env.Session.Status);
        Assert.False(provider.Active);
        Assert.False(env.Routing.IsRunning);
        Assert.False(env.Stages.HasRoute);
        Assert.Null(env.Stages.State);
        pause.Release.TrySetResult();
        Assert.True((await env.Connect.ExecuteAsync(MisuseTestEnvironment.Command(protocol))).Success);
        await env.Disconnect.ExecuteAsync();
        Assert.False(provider.Active);
    }

    [Theory]
    [InlineData("auth", VpnProtocol.OpenVpn)]
    [InlineData("adapter", VpnProtocol.OpenVpn)]
    [InlineData("route", VpnProtocol.OpenVpn)]
    [InlineData("egress", VpnProtocol.OpenVpn)]
    [InlineData("routing", VpnProtocol.OpenVpn)]
    [InlineData("auth", VpnProtocol.Sstp)]
    [InlineData("adapter", VpnProtocol.Sstp)]
    [InlineData("route", VpnProtocol.Sstp)]
    [InlineData("egress", VpnProtocol.Sstp)]
    [InlineData("routing", VpnProtocol.Sstp)]
    public async Task FailureAtEachStageRollsBackAndNextAttemptWorks(string failure, VpnProtocol protocol)
    {
        using var env = new MisuseTestEnvironment();
        var provider = protocol == VpnProtocol.OpenVpn ? env.OpenVpn : env.Sstp;
        provider.RejectAuthentication = failure == "auth";
        provider.MissingAdapter = failure == "adapter";
        env.Stages.FailRoute = failure == "route";
        env.Stages.FailEgress = failure == "egress";
        env.Routing.FailStartup = failure == "routing";
        Assert.False((await env.Connect.ExecuteAsync(MisuseTestEnvironment.Command(protocol))).Success);
        Assert.False(provider.Active);
        Assert.False(env.Routing.IsRunning);
        Assert.False(env.Stages.HasRoute);
        Assert.Null(env.Stages.State);
        provider.RejectAuthentication = provider.MissingAdapter = env.Stages.FailRoute = env.Stages.FailEgress = env.Routing.FailStartup = false;
        Assert.True((await env.Connect.ExecuteAsync(MisuseTestEnvironment.Command(protocol))).Success);
        await env.Disconnect.ExecuteAsync();
    }

    [Fact]
    public async Task CancelBeforeConnectAndRepeatedDisconnectAreSafe()
    {
        using var env = new MisuseTestEnvironment();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Connect.ExecuteAsync(MisuseTestEnvironment.Command(), cancelled.Token));
        Assert.Equal(0, env.OpenVpn.ConnectCount);
        await Task.WhenAll(env.Disconnect.ExecuteAsync(), env.Disconnect.ExecuteAsync());
        Assert.True((await env.Connect.ExecuteAsync(MisuseTestEnvironment.Command())).Success);
        Assert.False((await env.Connect.ExecuteAsync(MisuseTestEnvironment.Command(VpnProtocol.Sstp))).Success);
        Assert.Equal(0, env.Sstp.ConnectCount);
        await env.Disconnect.ExecuteAsync();
        Assert.True((await env.Connect.ExecuteAsync(MisuseTestEnvironment.Command(VpnProtocol.Sstp))).Success);
        await env.Disconnect.ExecuteAsync();
    }

    [Theory]
    [InlineData("999.2.3.4:443")]
    [InlineData("https://vpn.example")]
    [InlineData("vpn.example:wrong")]
    public async Task InvalidAddressDoesNotReachProvider(string address)
    {
        using var env = new MisuseTestEnvironment();
        var result = await env.Connect.ExecuteAsync(MisuseTestEnvironment.Command() with { ServerAddressRaw = address });
        Assert.False(result.Success);
        Assert.Equal(0, env.OpenVpn.ConnectCount);
        Assert.Equal(ConnectionStatus.Error, env.Session.Status);
    }
}
