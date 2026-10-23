using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Infrastructure.Routing;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class ProxiFyreStartupTests
{
    [Fact]
    public async Task StartupKeepsWaitingWhenInitializationTakesMoreThanTwoSeconds()
    {
        var directory = Directory.CreateTempSubdirectory("vpn-startup-").FullName;
        try
        {
            var session = new ProxiFyreLogSession(directory, 5000, TunnelProtocolScope.TcpAndUdp);
            var waiting = session.WaitUntilReadyAsync(() => false, () => 0, CancellationToken.None,
                TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(2100));
            Assert.False(waiting.IsCompleted);
            await File.WriteAllTextAsync(Path.Combine(directory, "engine.txt"), ReadyLog(5000));
            await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(TunnelProtocolScope.TcpOnly, "TCP")]
    [InlineData(TunnelProtocolScope.UdpOnly, "UDP")]
    public void SingleProtocolScopeOnlyRequiresItsOwnListener(TunnelProtocolScope scope, string protocol)
    {
        WithDirectory(directory =>
        {
            var session = new ProxiFyreLogSession(directory, 5000, scope);
            File.WriteAllText(Path.Combine(directory, "engine.txt"),
                $"Local IPv4 {protocol} proxy for 127.0.0.1:5000 is listening port: 5001\n" +
                "SOCKS5 Local Router instance started successfully.\n");
            Assert.True(session.ReadReady());
        });
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".log")]
    public void StartupRequiresBothProtocolsFromTheCurrentAttempt(string extension)
    {
        WithDirectory(directory =>
        {
            var file = Path.Combine(directory, "engine" + extension);
            var oldRun = ReadyLog(5000);
            File.WriteAllText(file, oldRun);
            var session = new ProxiFyreLogSession(directory, 5000, TunnelProtocolScope.TcpAndUdp);
            Assert.False(session.ReadReady());
            File.AppendAllText(file, ReadyLog(6000));
            Assert.False(session.ReadReady());
            File.AppendAllText(file, "Local IPv4 TCP proxy for 127.0.0.1:5000 is listening port: 5001\n");
            Assert.False(session.ReadReady());
            File.AppendAllText(file, "Local IPv4 UDP proxy for 127.0.0.1:5000 is listening port: 5002\n" +
                "SOCKS5 Local Router instance started successfully.\n");
            Assert.True(session.ReadReady());
        });
    }

    [Fact]
    public async Task ProcessExitIsReportedImmediatelyWithTheCurrentLogAndExitCode()
    {
        var directory = Directory.CreateTempSubdirectory("vpn-startup-").FullName;
        try
        {
            var session = new ProxiFyreLogSession(directory, 5000, TunnelProtocolScope.TcpAndUdp);
            await File.WriteAllTextAsync(Path.Combine(directory, "engine.txt"), "Failed to open NDISRD device\n");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.WaitUntilReadyAsync(() => true, () => 126, CancellationToken.None));
            Assert.Contains("126", error.Message);
            Assert.Contains("Failed to open NDISRD", error.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StartupHonorsCancellationAndTimesOutInsteadOfWaitingForever()
    {
        var directory = Directory.CreateTempSubdirectory("vpn-startup-").FullName;
        try
        {
            var session = new ProxiFyreLogSession(directory, 5000, TunnelProtocolScope.TcpOnly);
            await Assert.ThrowsAsync<TimeoutException>(() => session.WaitUntilReadyAsync(
                () => false, () => 0, CancellationToken.None, TimeSpan.FromMilliseconds(30)));
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.WaitUntilReadyAsync(() => false, () => 0, stop.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PartialLogLineDoesNotConfirmStartupUntilComplete()
    {
        WithDirectory(directory =>
        {
            var session = new ProxiFyreLogSession(directory, 5000, TunnelProtocolScope.TcpAndUdp);
            var file = Path.Combine(directory, "engine.txt");
            File.WriteAllText(file, ReadyLog(5000).TrimEnd('\n'));
            Assert.False(session.ReadReady());
            File.AppendAllText(file, "\n");
            Assert.True(session.ReadReady());
        });
    }

    [Fact]
    public void RoutingResetPreservesTheExitTestButNewSessionResetClearsIt()
    {
        var diagnostics = new TunnelDiagnostics();
        var result = new EgressSelfTestResult(true, "TCP e UDP disponíveis");
        diagnostics.SelfTestCompleted(result);
        diagnostics.Reset(preserveEgressSelfTest: true);
        Assert.Same(result, diagnostics.EgressSelfTest);
        diagnostics.Reset();
        Assert.Null(diagnostics.EgressSelfTest);
    }

    private static string ReadyLog(int port) =>
        $"Local IPv4 TCP proxy for 127.0.0.1:{port} is listening port: 5001\n" +
        $"Local IPv4 UDP proxy for 127.0.0.1:{port} is listening port: 5002\n" +
        "SOCKS5 Local Router instance started successfully.\n";

    private static void WithDirectory(Action<string> action)
    {
        var directory = Directory.CreateTempSubdirectory("vpn-startup-").FullName;
        try { action(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
