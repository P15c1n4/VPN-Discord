using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.Exceptions;
using ProxyDiscord.Domain.ValueObjects;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class InputMisuseTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(":443")]
    [InlineData("vpn.example:")]
    [InlineData("vpn.example:abc")]
    [InlineData("vpn.example:0")]
    [InlineData("vpn.example:65536")]
    [InlineData("999.8.0.1:443")]
    [InlineData("127.1:443")]
    [InlineData("https://vpn.example:443")]
    [InlineData("vpn example:443")]
    [InlineData("vpn.example/path:443")]
    [InlineData("[2001:db8::1:443")]
    [InlineData("[::1]garbage")]
    public void InvalidEndpointIsRejected(string address) =>
        Assert.Throws<AddressParseException>(() => HostEndpoint.Parse(address, 443));

    [Theory]
    [InlineData("vpn.example", "vpn.example", 443)]
    [InlineData(" vpn.example:1194 ", "vpn.example", 1194)]
    [InlineData("127.0.0.1:443", "127.0.0.1", 443)]
    [InlineData("192.0.2.1:65535", "192.0.2.1", 65535)]
    [InlineData("[::1]:1194", "::1", 1194)]
    [InlineData("2001:db8::1", "2001:db8::1", 443)]
    public void ValidEndpointRoundTrips(string address, string host, int port)
    {
        var endpoint = HostEndpoint.Parse(address, 443);
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(endpoint, HostEndpoint.Parse(endpoint.ToString(), 443));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only a comment\nclient\n")]
    [InlineData("client\n127.0.0.1 443\n")]
    [InlineData("remote vpn.example 0\n")]
    [InlineData("remote vpn.example 65536\n")]
    [InlineData("remote vpn.example abc\n")]
    [InlineData("remote https://vpn.example 443\n")]
    [InlineData("remote 999.8.0.1 443\n")]
    [InlineData("remote vpn.example 443\nproto banana\n")]
    [InlineData("remote vpn.example 443 sstp\n")]
    [InlineData("remote vpn.example 443\nca missing.crt\n")]
    [InlineData("remote vpn.example 443\n<auth-user-pass>\nalice\nsecret\n")]
    public async Task InvalidProfileReturnsReadableError(string content)
    {
        using var env = new MisuseTestEnvironment();
        var result = await env.LoadProfile.ExecuteAsync(await env.WriteProfileAsync(content));
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Equal(0, env.OpenVpn.ConnectCount);
    }

    [Theory]
    [InlineData("remote vpn.example 443\nproto tcp-client\n", TransportProtocol.Tcp)]
    [InlineData("remote vpn.example 1194\nproto udp\n", TransportProtocol.Udp)]
    [InlineData("remote vpn.example\n", TransportProtocol.Udp)]
    [InlineData("remote 127.0.0.1 443 tcp\n", TransportProtocol.Tcp)]
    [InlineData("remote vpn.example 443 tcp\nproto udp\n", TransportProtocol.Tcp)]
    [InlineData("remote vpn.example 443 # comment\r\nproto tcp-client\r\n", TransportProtocol.Tcp)]
    public async Task ValidProfilesDetectTransportAndSupportLoopback(string content, TransportProtocol expected)
    {
        using var env = new MisuseTestEnvironment();
        var result = await env.LoadProfile.ExecuteAsync(await env.WriteProfileAsync(content + "auth-user-pass\n"));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.Profile!.Transport);
        Assert.Equal(OpenVpnAuthenticationKind.InteractivePrompt, result.Profile.Authentication.Kind);
    }

    [Fact]
    public async Task MissingOrOversizedProfilesDoNotCrashOrConnect()
    {
        using var env = new MisuseTestEnvironment();
        Assert.False((await env.LoadProfile.ExecuteAsync(Path.Combine(env.DirectoryPath, "missing.ovpn"))).Success);
        Assert.False((await env.LoadProfile.ExecuteAsync("\0bad-path")).Success);
        var path = await env.WriteProfileAsync("remote vpn.example 443\n");
        using (var stream = File.OpenWrite(path)) stream.SetLength(4 * 1024 * 1024 + 1);
        Assert.False((await env.LoadProfile.ExecuteAsync(path)).Success);
    }

    [Fact]
    public async Task CancelledFileReadPropagatesCancellation()
    {
        using var env = new MisuseTestEnvironment();
        var path = await env.WriteProfileAsync("remote vpn.example 443\nauth-user-pass\n");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.LoadProfile.ExecuteAsync(path, cancelled.Token));
    }
}
