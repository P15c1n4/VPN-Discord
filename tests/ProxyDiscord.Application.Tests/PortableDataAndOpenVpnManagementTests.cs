using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Infrastructure.OpenVpn;
using ProxyDiscord.Infrastructure.StateStore;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class PortableDataAndOpenVpnManagementTests
{
    [Fact]
    public async Task CredentialsAreCachedInMemoryAndPersistedOnlyInUserAuthJson()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            var store = CreateTestStore(directory);
            await store.InitializeAsync();
            await store.WriteAsync(new UserConfiguration(true, true));
            await store.SaveAsync("OpenVpn:vpn.example:443", "alice", "local-secret");

            var config = await File.ReadAllTextAsync(Path.Combine(directory, "config.json"));
            Assert.DoesNotContain("local-secret", config, StringComparison.Ordinal);
            using var settings = JsonDocument.Parse(config);
            Assert.False(settings.RootElement.TryGetProperty("servers", out _));

            var auth = await File.ReadAllTextAsync(Path.Combine(directory, "user_auth.json"));
            Assert.DoesNotContain("local-secret", auth, StringComparison.Ordinal);
            using var credentialsDocument = JsonDocument.Parse(auth);
            Assert.True(credentialsDocument.RootElement.GetProperty("servers")
                .TryGetProperty("OpenVpn:vpn.example:443", out _));

            var restartedStore = CreateTestStore(directory);
            var credentials = await restartedStore.FindAsync("OpenVpn:vpn.example:443");
            Assert.Equal("alice", credentials?.Username);
            Assert.Equal("local-secret", credentials?.Password);
        });
    }

    [Fact]
    public async Task CombinedConfigIsMigratedToSeparateCredentialFile()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            const string serverKey = "OpenVpn:vpn.example:443";
            await File.WriteAllTextAsync(
                Path.Combine(directory, "config.json"),
                CombinedConfig(serverKey, "alice", "protected-secret"));

            var migratedStore = CreateTestStore(directory);
            await migratedStore.InitializeAsync();

            Assert.Equal("protected-secret", (await migratedStore.FindAsync(serverKey))?.Password);
            var settings = await migratedStore.ReadAsync();
            Assert.True(settings.SaveCredentialsEnabled);
            Assert.True(settings.SavePasswordAfterConnectionEnabled);
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "config.json")));
            Assert.False(config.RootElement.TryGetProperty("servers", out _));
            using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "user_auth.json")));
            Assert.True(auth.RootElement.GetProperty("servers").TryGetProperty(serverKey, out _));
        });
    }

    [Fact]
    public async Task ExistingCredentialFileRemainsInUseWhenConfigHasOnlySettings()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            const string serverKey = "OpenVpn:vpn.example:443";
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"),
                "{\"saveCredentialsEnabled\":true,\"savePasswordAfterConnectionEnabled\":true}");
            var authPath = Path.Combine(directory, "user_auth.json");
            var original = AuthDocument(serverKey, "alice", "old-secret");
            await File.WriteAllTextAsync(authPath, original);

            var store = CreateTestStore(directory);
            await store.InitializeAsync();

            Assert.Equal("old-secret", (await store.FindAsync(serverKey))?.Password);
            Assert.Equal(original, await File.ReadAllTextAsync(authPath));
        });
    }

    [Fact]
    public async Task MigrationMergesDistinctServersWithoutLosingExistingCredentials()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            const string newKey = "OpenVpn:new.example:443";
            const string oldKey = "OpenVpn:old.example:443";
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"),
                CombinedConfig(newKey, "new-user", "new-secret"));
            await File.WriteAllTextAsync(Path.Combine(directory, "user_auth.json"),
                AuthDocument(oldKey, "old-user", "old-secret"));

            var store = CreateTestStore(directory);
            await store.InitializeAsync();

            Assert.Equal("new-secret", (await store.FindAsync(newKey))?.Password);
            Assert.Equal("old-secret", (await store.FindAsync(oldKey))?.Password);
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "config.json")));
            Assert.False(config.RootElement.TryGetProperty("servers", out _));
        });
    }

    [Fact]
    public async Task ConflictingCredentialFilesArePreservedForReview()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            const string serverKey = "OpenVpn:vpn.example:443";
            var configPath = Path.Combine(directory, "config.json");
            var authPath = Path.Combine(directory, "user_auth.json");
            var combined = CombinedConfig(serverKey, "alice", "new-secret");
            var separate = AuthDocument(serverKey, "alice", "other-secret");
            await File.WriteAllTextAsync(configPath, combined);
            await File.WriteAllTextAsync(authPath, separate);

            await Assert.ThrowsAsync<InvalidDataException>(() => CreateTestStore(directory).InitializeAsync());

            Assert.Equal(combined, await File.ReadAllTextAsync(configPath));
            Assert.Equal(separate, await File.ReadAllTextAsync(authPath));
        });
    }

    [Fact]
    public async Task FailedCredentialWriteKeepsCombinedConfigUntouched()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            var configPath = Path.Combine(directory, "config.json");
            var combined = CombinedConfig("OpenVpn:vpn.example:443", "alice", "secret");
            await File.WriteAllTextAsync(configPath, combined);
            Directory.CreateDirectory(Path.Combine(directory, "user_auth.json"));

            var failure = await Assert.ThrowsAnyAsync<Exception>(() => CreateTestStore(directory).InitializeAsync());
            Assert.True(failure is IOException or UnauthorizedAccessException);

            Assert.Equal(combined, await File.ReadAllTextAsync(configPath));
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"servers\":null}")]
    [InlineData("{}")]
    [InlineData("{\"other\":true}")]
    public async Task InvalidCredentialFileIsNotOverwritten(string invalidLegacyJson)
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            var legacyPath = Path.Combine(directory, "user_auth.json");
            await File.WriteAllTextAsync(legacyPath, invalidLegacyJson);

            await Assert.ThrowsAsync<InvalidDataException>(() => CreateTestStore(directory).InitializeAsync());

            Assert.Equal(invalidLegacyJson, await File.ReadAllTextAsync(legacyPath));
            Assert.False(File.Exists(Path.Combine(directory, "config.json")));
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"servers\":null}")]
    public async Task InvalidConfigIsNotOverwritten(string invalidConfigJson)
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            var configPath = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(configPath, invalidConfigJson);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                CreateTestStore(directory).WriteAsync(new UserConfiguration(true, true)));

            Assert.Equal(invalidConfigJson, await File.ReadAllTextAsync(configPath));
        });
    }

    [Fact]
    public void OpenVpnSessionUsesManagementCredentialsAndJsonTunnelMetadata()
    {
        WithTemporaryDirectory(directory =>
        {
            var writer = new OpenVpnProfileWriter(NullLogger<OpenVpnProfileWriter>.Instance, directory);
            var config = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "client\nremote vpn.example 443\nconnect-retry 10 300\nconnect-retry-max 2\n" +
                "connect-timeout 30\nserver-poll-timeout 45\nresolv-retry 5\n" +
                "script-security 3\nup C:\\temp\\untrusted.bat\nplugin C:\\temp\\untrusted.dll\n" +
                "config extra.ovpn\nmanagement-client-user untrusted-user\n" +
                "management 0.0.0.0 12345\ndaemon\n"));
            using var profile = writer.Write(config, "alice", "secret", "Discord VPN", 45321);
            var generated = File.ReadAllText(profile.ConfigPath);

            Assert.Contains("auth-user-pass", generated, StringComparison.Ordinal);
            Assert.Contains("management-query-passwords", generated, StringComparison.Ordinal);
            Assert.Contains(
                $"management 127.0.0.1 45321 \"{profile.ManagementPasswordFilePath.Replace("\\", "\\\\")}\"",
                generated,
                StringComparison.Ordinal);
            Assert.Equal(64, profile.ManagementPassword.Length);
            Assert.Equal(profile.ManagementPassword, File.ReadAllText(profile.ManagementPasswordFilePath));
            Assert.DoesNotContain("connect-retry ", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("connect-retry-max", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("connect-timeout", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("server-poll-timeout", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("resolv-retry", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("untrusted", generated, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("extra.ovpn", generated, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("management 0.0.0.0", generated, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("management-client-user", generated, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("script-security 3", generated, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\ndaemon", generated, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("script-security 2", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", generated, StringComparison.Ordinal);
            Assert.EndsWith("tunnel.json", profile.TunnelInfoPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(profile.Directory, "auth.txt")));
            Assert.False(File.Exists(Path.Combine(profile.Directory, "openvpn.log")));
        });
    }

    [Fact]
    public void OpenVpnSessionPreservesSelectedProfileCredentialsButRejectsItsScripts()
    {
        WithTemporaryDirectory(directory =>
        {
            var writer = new OpenVpnProfileWriter(NullLogger<OpenVpnProfileWriter>.Instance, directory);
            var input =
                "client\nremote vpn.example 443\n<auth-user-pass>\nalice\nprofile-secret\n</auth-user-pass>\n" +
                "up C:\\temp\\untrusted.bat\n";
            var config = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));
            using var profile = writer.Write(
                config, null, null, "Discord VPN", 45321, useProfileCredentials: true);
            var generated = File.ReadAllText(profile.ConfigPath);

            Assert.Contains("<auth-user-pass>", generated, StringComparison.Ordinal);
            Assert.Contains("profile-secret", generated, StringComparison.Ordinal);
            Assert.DoesNotContain("untrusted.bat", generated, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("management-query-passwords", generated, StringComparison.Ordinal);
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

    [Fact]
    public async Task ManagementClientDoesNotAcceptRejectedStartupCommand()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true };
            Assert.Equal("state on", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("ERROR: command rejected");
        });

        using var client = new OpenVpnManagementClient(NullLogger.Instance);
        Assert.False(await client.ConnectAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ManagementClientAuthenticatesBeforeSendingCommands()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(socket.GetStream(), new UTF8Encoding(false));
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

            await writer.WriteLineAsync("ENTER PASSWORD:");
            Assert.Equal("session-only-secret", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: password is correct");
            Assert.Equal("state on", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: state on");
            Assert.Equal("hold release", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: hold release");
            await writer.WriteLineAsync(">STATE:2026-09-26 12:00:00,CONNECTED,SUCCESS,10.8.0.2,10.8.0.1");
        });

        using var client = new OpenVpnManagementClient(
            NullLogger.Instance, managementPassword: "session-only-secret");
        Assert.True(await client.ConnectAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(
            OpenVpnState.Connected,
            await client.WaitForConnectedAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ManagementClientAuthenticatesWhenServerDoesNotSendPasswordPromptFirst()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(socket.GetStream(), new UTF8Encoding(false));
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

            Assert.Equal("session-only-secret", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: password is correct");
            Assert.Equal("state on", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: state on");
            Assert.Equal("hold release", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: hold release");
        });

        using var client = new OpenVpnManagementClient(
            NullLogger.Instance, managementPassword: "session-only-secret");
        Assert.True(await client.ConnectAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ManagementClientStopsWhenManagementPasswordIsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(socket.GetStream(), new UTF8Encoding(false));
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

            await writer.WriteLineAsync("ENTER PASSWORD:");
            Assert.Equal("wrong-secret", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("ERROR: bad password");
        });

        using var client = new OpenVpnManagementClient(
            NullLogger.Instance, managementPassword: "wrong-secret");
        Assert.False(await client.ConnectAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ManagementClientRequestsPrivateKeyPassphraseWhenOpenVpnPrompts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var prompt = new TestOpenVpnInteractivePrompt(new OpenVpnPromptResponse(null, "key-secret", null));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(socket.GetStream(), new UTF8Encoding(false));
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            Assert.Equal("state on", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: state on");
            Assert.Equal("hold release", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: hold release");
            await writer.WriteLineAsync(">PASSWORD:Need 'Private Key' password");
            Assert.Equal("password \"Private Key\" \"key-secret\"", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync(">STATE:2026-09-26 12:00:00,CONNECTED,SUCCESS,10.8.0.2,10.8.0.1");
        });

        using var client = new OpenVpnManagementClient(NullLogger.Instance, interactivePrompt: prompt);
        Assert.True(await client.ConnectAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(OpenVpnState.Connected,
            await client.WaitForConnectedAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(OpenVpnPromptKind.PrivateKeyPassphrase, Assert.Single(prompt.Requests).Kind);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ManagementClientFormatsStaticChallengeResponseAsScrv1()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var prompt = new TestOpenVpnInteractivePrompt(
            new OpenVpnPromptResponse("alice", "vpn-secret", "123456"));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(socket.GetStream(), new UTF8Encoding(false));
            using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            Assert.Equal("state on", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: state on");
            Assert.Equal("hold release", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync("SUCCESS: hold release");
            await writer.WriteLineAsync(">PASSWORD:Need 'Auth' username/password SC:E:Token code");
            Assert.Equal("username \"Auth\" \"alice\"", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("vpn-secret"));
            var answer = Convert.ToBase64String(Encoding.UTF8.GetBytes("123456"));
            Assert.Equal($"password \"Auth\" \"SCRV1:{encoded}:{answer}\"",
                await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await writer.WriteLineAsync(">STATE:2026-09-26 12:00:00,CONNECTED,SUCCESS,10.8.0.2,10.8.0.1");
        });

        using var client = new OpenVpnManagementClient(
            NullLogger.Instance, "alice", "vpn-secret", interactivePrompt: prompt);
        Assert.True(await client.ConnectAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(OpenVpnState.Connected,
            await client.WaitForConnectedAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        var capturedPrompt = Assert.Single(prompt.Requests);
        Assert.Equal(OpenVpnPromptKind.StaticChallenge, capturedPrompt.Kind);
        Assert.Equal("Token code", capturedPrompt.Message);
        Assert.True(capturedPrompt.ChallengeResponseEcho);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
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

    private sealed class TestOpenVpnInteractivePrompt(OpenVpnPromptResponse response) : IOpenVpnInteractivePrompt
    {
        public List<OpenVpnPromptRequest> Requests { get; } = [];

        public Task<OpenVpnPromptResponse?> RequestAsync(
            OpenVpnPromptRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<OpenVpnPromptResponse?>(response);
        }
    }

    private static JsonApplicationDataStore CreateTestStore(string directory) => new(
        NullLogger<JsonApplicationDataStore>.Instance,
        directory,
        value => $"test:{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}",
        protectedValue => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["test:".Length..])));

    private static string AuthDocument(string serverKey, string username, string password) =>
        JsonSerializer.Serialize(new
        {
            servers = new Dictionary<string, object>
            {
                [serverKey] = new { username, protectedPassword = ProtectForTest(password) },
            },
        });

    private static string CombinedConfig(string serverKey, string username, string password) =>
        JsonSerializer.Serialize(new
        {
            saveCredentialsEnabled = true,
            savePasswordAfterConnectionEnabled = true,
            servers = new Dictionary<string, object>
            {
                [serverKey] = new { username, protectedPassword = ProtectForTest(password) },
            },
        });

    private static string ProtectForTest(string value) =>
        $"test:{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}";

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
