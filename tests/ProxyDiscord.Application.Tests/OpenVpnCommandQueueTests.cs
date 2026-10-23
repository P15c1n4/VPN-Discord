using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Infrastructure.OpenVpn;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class OpenVpnCommandQueueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundledOpenVpnRepeatedlyAcceptsCredentialsBeforeStartingTcp(bool authenticatedManagement)
    {
        var executable = FindOpenVpn();
        var directory = Path.Combine(Path.GetTempPath(), $"ProxyDiscord-CommandQueue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string localPassword = "loopback-management-secret";
            var passwordPath = Path.Combine(directory, "management.pw");
            await File.WriteAllTextAsync(passwordPath, localPassword);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var remote = new TcpListener(IPAddress.Loopback, 0);
                remote.Start();
                var managementPort = ReservePort();
                var info = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                string[] arguments = ["--client", "--dev", "null", "--proto", "tcp-client",
                    "--remote", "127.0.0.1", ((IPEndPoint)remote.LocalEndpoint).Port.ToString(),
                    "--peer-fingerprint", string.Join(':', Enumerable.Repeat("00", 32)),
                    "--auth-user-pass", "--management", "127.0.0.1", managementPort.ToString()];
                foreach (var argument in arguments) info.ArgumentList.Add(argument);
                if (authenticatedManagement) info.ArgumentList.Add(passwordPath);
                foreach (var argument in new[] { "--management-query-passwords", "--management-hold", "--verb", "3" })
                    info.ArgumentList.Add(argument);

                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var client = new OpenVpnManagementClient(new TestLogger(), "diagnostic-user", "diagnostic-secret",
                    authenticatedManagement ? localPassword : null);
                Task<OpenVpnState>? waiting = null;
                try
                {
                    Assert.True(await client.ConnectAsync(managementPort, TimeSpan.FromSeconds(5), cancellation.Token),
                        client.StartupFailureReason);
                    waiting = client.WaitForConnectedAsync(TimeSpan.FromSeconds(6), cancellation.Token);
                    // No external server, driver or certificate is involved. Getting here requires
                    // the real OpenVPN process to accept BOTH management credential commands.
                    using var accepted = await remote.AcceptTcpClientAsync(cancellation.Token);
                    Assert.Equal(IPAddress.Loopback, ((IPEndPoint)accepted.Client.RemoteEndPoint!).Address);
                    await cancellation.CancelAsync();
                    await waiting;
                    await client.RequestShutdownAsync();
                }
                finally
                {
                    await cancellation.CancelAsync();
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    if (waiting is not null) await waiting;
                    await Task.WhenAll(output, errors);
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public Task CredentialNotificationBeforeStartupAckDoesNotConsumeOrBypassThatAck() => RunServerAsync(async peer =>
    {
        Assert.Equal("state on", await peer.ReadAsync());
        await peer.WriteAsync(">PASSWORD:Need 'Auth' username/password");
        var next = peer.ReadAsync();
        await Task.Delay(40);
        Assert.False(next.IsCompleted);
        await peer.WriteAsync("SUCCESS: state on");
        Assert.Equal("username \"Auth\" \"alice\"", await next);
        await peer.WriteAsync("SUCCESS: 'Auth' username entered, but not yet verified");
        Assert.Equal("password \"Auth\" \"secret\"", await peer.ReadAsync());
        await peer.WriteAsync("SUCCESS: 'Auth' password entered, but not yet verified");
        Assert.Equal("hold off", await peer.ReadAsync());
        await peer.WriteAsync("SUCCESS: hold flag set to OFF");
        Assert.Equal("hold release", await peer.ReadAsync());
        await peer.WriteAsync("SUCCESS: hold release");
        await peer.WriteAsync(">STATE:1,CONNECTED,SUCCESS,10.8.0.2");
    }, async client =>
    {
        Assert.True(await client.ConnectAsync(_port.Value, TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(OpenVpnState.Connected, await client.WaitForConnectedAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
    });

    [Fact]
    public Task StateNotificationsDoNotReleaseTheNextCredentialCommand() => RunServerAsync(async peer =>
    {
        await peer.HandshakeAsync();
        await peer.WriteAsync(">PASSWORD:Need 'Auth' username/password");
        Assert.Equal("username \"Auth\" \"alice\"", await peer.ReadAsync());
        var password = peer.ReadAsync();
        await peer.WriteAsync(">STATE:1,TCP_CONNECT,,,,,,");
        await peer.WriteAsync(">INFO:notification between command and ACK");
        await Task.Delay(40);
        Assert.False(password.IsCompleted);
        await peer.WriteAsync("SUCCESS: 'Auth' username entered, but not yet verified");
        Assert.Equal("password \"Auth\" \"secret\"", await password);
        await peer.WriteAsync("SUCCESS: 'Auth' password entered, but not yet verified");
        await peer.WriteAsync(">STATE:2,CONNECTED,SUCCESS,10.8.0.2");
    }, ConnectAndWaitAsync);

    [Fact]
    public Task RejectedUsernameDropsQueuedPasswordAndDoesNotExposeResponseValues() => RunServerAsync(async peer =>
    {
        await peer.HandshakeAsync();
        await peer.WriteAsync(">PASSWORD:Need 'Auth' username/password");
        Assert.Equal("username \"Auth\" \"alice\"", await peer.ReadAsync());
        await peer.WriteAsync("ERROR: rejected secret-sensitive-value");
        Assert.Equal("signal SIGTERM", await peer.ReadAsync());
    }, async client =>
    {
        Assert.True(await client.ConnectAsync(_port.Value, TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(OpenVpnState.AuthFailed, await client.WaitForConnectedAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Contains("username (Auth)", client.LastStateMessage);
        Assert.DoesNotContain("secret-sensitive-value", client.LastStateMessage);
        await client.RequestShutdownAsync();
    });

    [Theory]
    [InlineData(false, "username")]
    [InlineData(true, "username")]
    [InlineData(false, "password")]
    [InlineData(true, "password")]
    public Task MissingCredentialAckExpiresDuringInitialConnectionAndReauthentication(bool reauthentication, string unconfirmed) => RunServerAsync(async peer =>
    {
        await peer.HandshakeAsync();
        if (reauthentication) await peer.WriteAsync(">STATE:1,CONNECTED,SUCCESS,10.8.0.2");
        await peer.WriteAsync(">PASSWORD:Need 'Auth' username/password");
        Assert.Equal("username \"Auth\" \"alice\"", await peer.ReadAsync());
        if (unconfirmed == "password")
        {
            await peer.WriteAsync("SUCCESS: 'Auth' username entered, but not yet verified");
            Assert.Equal("password \"Auth\" \"secret\"", await peer.ReadAsync());
        }
        Assert.Equal("signal SIGTERM", await peer.ReadAsync());
    }, async client =>
    {
        Assert.True(await client.ConnectAsync(_port.Value, TimeSpan.FromSeconds(3), CancellationToken.None));
        if (reauthentication)
        {
            Assert.Equal(OpenVpnState.Connected, await client.WaitForConnectedAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
            await client.MonitorAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        }
        else
        {
            Assert.Equal(OpenVpnState.AuthFailed, await client.WaitForConnectedAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        }
        Assert.Equal(OpenVpnState.AuthFailed, client.State);
        Assert.Contains("não confirmou", client.LastStateMessage);
        Assert.Contains($"{unconfirmed} (Auth)", client.LastStateMessage);
        await client.RequestShutdownAsync();
    }, TimeSpan.FromMilliseconds(150));

    [Fact]
    public Task CancellationCanInterruptAnUnconfirmedUsernameAndSendShutdown() => RunServerAsync(async peer =>
    {
        await peer.HandshakeAsync();
        await peer.WriteAsync(">PASSWORD:Need 'Auth' username/password");
        Assert.Equal("username \"Auth\" \"alice\"", await peer.ReadAsync());
        Assert.Equal("signal SIGTERM", await peer.ReadAsync());
    }, async client =>
    {
        Assert.True(await client.ConnectAsync(_port.Value, TimeSpan.FromSeconds(3), CancellationToken.None));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await client.WaitForConnectedAsync(TimeSpan.FromSeconds(3), cancelled.Token).WaitAsync(TimeSpan.FromSeconds(2));
        await client.RequestShutdownAsync();
    });

    private static readonly AsyncLocal<int> _port = new();
    private static async Task RunServerAsync(Func<Peer, Task> serve, Func<OpenVpnManagementClient, Task> useClient,
        TimeSpan? commandTimeout = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port.Value = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var peer = new Peer(socket);
            await serve(peer);
        });
        var logger = new TestLogger();
        using var client = new OpenVpnManagementClient(logger, "alice", "secret", commandResponseTimeout: commandTimeout);
        await useClient(client);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("secret", StringComparison.Ordinal));
    }

    private static async Task ConnectAndWaitAsync(OpenVpnManagementClient client)
    {
        Assert.True(await client.ConnectAsync(_port.Value, TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(OpenVpnState.Connected, await client.WaitForConnectedAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    private sealed class Peer(TcpClient socket) : IDisposable
    {
        private readonly StreamReader _reader = new(socket.GetStream(), new UTF8Encoding(false));
        private readonly StreamWriter _writer = new(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        public Task<string?> ReadAsync() => _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        public Task WriteAsync(string line) => _writer.WriteLineAsync(line);
        public async Task HandshakeAsync()
        {
            foreach (var command in new[] { "state on", "hold off", "hold release" })
            {
                Assert.Equal(command, await ReadAsync());
                await WriteAsync($"SUCCESS: {command}");
            }
        }
        public void Dispose() { _reader.Dispose(); _writer.Dispose(); }
    }

    private sealed class TestLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, error));
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindOpenVpn()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "vendor", "openvpn", "bin", "openvpn.exe");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("OpenVPN real não encontrado para o teste em loopback.");
    }
}
