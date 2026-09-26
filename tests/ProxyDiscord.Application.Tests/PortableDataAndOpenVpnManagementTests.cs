using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Infrastructure.OpenVpn;
using ProxyDiscord.Infrastructure.StateStore;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class PortableDataAndOpenVpnManagementTests
{
    [Fact]
    public async Task CredentialsAreCachedAndPersistedOnlyInConfigJson()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            var store = CreateTestStore(directory);
            await store.InitializeAsync();
            await store.WriteAsync(new UserConfiguration(true, true));
            await store.SaveAsync("OpenVpn:vpn.example:443", "alice", "local-secret");

            var config = await File.ReadAllTextAsync(Path.Combine(directory, "config.json"));
            Assert.DoesNotContain("local-secret", config, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, "user_auth.json")));

            var restartedStore = CreateTestStore(directory);
            var credentials = await restartedStore.FindAsync("OpenVpn:vpn.example:443");
            Assert.Equal("alice", credentials?.Username);
            Assert.Equal("local-secret", credentials?.Password);
        });
    }

    [Fact]
    public async Task LegacyUserAuthJsonIsMergedThenRemoved()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            const string serverKey = "OpenVpn:vpn.example:443";
            var seedStore = CreateTestStore(directory);
            await seedStore.SaveAsync(serverKey, "alice", "protected-secret");
            using var seed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "config.json")));
            var entry = seed.RootElement.GetProperty("servers").GetProperty(serverKey).GetRawText();

            await File.WriteAllTextAsync(
                Path.Combine(directory, "config.json"),
                "{\"saveCredentialsEnabled\":true,\"savePasswordAfterConnectionEnabled\":true}");
            await File.WriteAllTextAsync(
                Path.Combine(directory, "user_auth.json"),
                $"{{\"servers\":{{\"{serverKey}\":{entry}}}}}");

            var migratedStore = CreateTestStore(directory);
            await migratedStore.InitializeAsync();

            Assert.Equal("protected-secret", (await migratedStore.FindAsync(serverKey))?.Password);
            Assert.False(File.Exists(Path.Combine(directory, "user_auth.json")));
            using var migrated = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "config.json")));
            Assert.True(migrated.RootElement.GetProperty("servers").TryGetProperty(serverKey, out _));
        });
    }

    [Fact]
    public void OpenVpnSessionUsesManagementCredentialsAndJsonTunnelMetadata()
    {
        WithTemporaryDirectory(directory =>
        {
            var writer = new OpenVpnProfileWriter(NullLogger<OpenVpnProfileWriter>.Instance, directory);
            var config = Convert.ToBase64String(Encoding.UTF8.GetBytes("client\nremote vpn.example 443\n"));
            using var profile = writer.Write(config, "alice", "secret", "Discord VPN", 45321);
            var generated = File.ReadAllText(profile.ConfigPath);

            Assert.Contains("auth-user-pass", generated, StringComparison.Ordinal);
            Assert.Contains("management-query-passwords", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", generated, StringComparison.Ordinal);
            Assert.EndsWith("tunnel.json", profile.TunnelInfoPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(profile.Directory, "auth.txt")));
            Assert.False(File.Exists(Path.Combine(profile.Directory, "openvpn.log")));
        });
    }

    [Fact]
    public async Task ManagementClientResendsInMemoryCredentialsForInitialAndReauthenticationChallenges()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestReauthentication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(socket.GetStream(), new UTF8Encoding(false));
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

            Assert.Equal("state on", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: state on");
            Assert.Equal("hold release", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: hold release");
            await writer.WriteLineAsync(">PASSWORD:Need 'Auth' username/password");
            Assert.Equal("username \"Auth\" \"alícia\"", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("password \"Auth\" \"sëc\\\"ret\\\\x\"", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync(">STATE:2026-09-26 12:00:00,CONNECTED,SUCCESS,10.8.0.2,10.8.0.1");

            await requestReauthentication.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await writer.WriteLineAsync(">PASSWORD:Need 'Auth' username/password");
            Assert.Equal("username \"Auth\" \"alícia\"", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("password \"Auth\" \"sëc\\\"ret\\\\x\"", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        });

        using var client = new OpenVpnManagementClient(
            NullLogger.Instance, "alícia", "sëc\"ret\\x");
        Assert.True(await client.ConnectAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(
            OpenVpnState.Connected,
            await client.WaitForConnectedAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

        using var monitorCancellation = new CancellationTokenSource();
        var monitor = client.MonitorAsync(monitorCancellation.Token);
        requestReauthentication.TrySetResult();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        monitorCancellation.Cancel();
        await monitor.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ProxyDiscordTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonApplicationDataStore CreateTestStore(string directory) => new(
        NullLogger<JsonApplicationDataStore>.Instance,
        directory,
        value => $"test:{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}",
        protectedValue => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["test:".Length..])));

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ProxyDiscordTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
