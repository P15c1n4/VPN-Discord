using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Infrastructure.Routing;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class ProxiFyreSocksEndpointTests
{
    [Fact]
    public async Task EndpointNegotiatesNoAuthenticationAndRejectsUnsupportedCommand()
    {
        var endpoint = new LocalSocks5VpnEndpoint(
            new TunnelDiagnostics(),
            NullLogger<LocalSocks5VpnEndpoint>.Instance);
        endpoint.Start(
            new VpnAdapterInfo("10.8.0.2", 9, 0, "10.8.0.1"),
            new TunnelDnsSettings(TunnelDnsSettings.GOOGLE_PUBLIC_DNS));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var negotiation = new byte[2];
            await stream.ReadExactlyAsync(negotiation);
            Assert.Equal(new byte[] { 5, 0 }, negotiation);

            await stream.WriteAsync(new byte[] { 5, 2, 0, 1, 127, 0, 0, 1, 0, 80 });
            var response = new byte[10];
            await stream.ReadExactlyAsync(response);
            Assert.Equal((byte)5, response[0]);
            Assert.Equal((byte)7, response[1]);
        }
        finally
        {
            await endpoint.StopAsync();
        }
    }
}
