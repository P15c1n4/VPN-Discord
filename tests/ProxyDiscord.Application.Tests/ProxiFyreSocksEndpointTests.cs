using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Infrastructure.Routing;
using Xunit;

namespace ProxyDiscord.Application.Tests;

[Collection("SOCKS5 socket lifetime")]
public sealed class ProxiFyreSocksEndpointTests
{
    [Fact]
    public async Task ClosingOneUdpAssociationKeepsAnotherAssociationWorking()
    {
        var endpoint = new LocalSocks5VpnEndpoint(
            new TunnelDiagnostics(), NullLogger<LocalSocks5VpnEndpoint>.Instance);
        endpoint.Start(
            new VpnAdapterInfo("127.0.0.1", (uint)NetworkInterface.LoopbackInterfaceIndex, 0, "127.0.0.1"),
            new TunnelDnsSettings(TunnelDnsSettings.GOOGLE_PUBLIC_DNS));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var echoServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var datagrams = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            var first = await OpenUdpAssociationAsync(endpoint.Port, timeout.Token);
            using var firstControl = first.Control;
            var second = await OpenUdpAssociationAsync(endpoint.Port, timeout.Token);
            using var secondControl = second.Control;
            await ExchangeDatagramAsync(datagrams, echoServer, first.Relay, timeout.Token);
            await ExchangeDatagramAsync(datagrams, echoServer, second.Relay, timeout.Token);
            firstControl.Dispose();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await ExchangeDatagramAsync(datagrams, echoServer, second.Relay, timeout.Token);
            }
        }
        finally
        {
            await endpoint.StopAsync().WaitAsync(timeout.Token);
        }
    }

    [Theory]
    [InlineData("close")]
    [InlineData("reset")]
    [InlineData("stop")]
    public async Task ClosingUdpAssociationsObservesPendingSocketOperations(string termination)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            foreach (var exception in args.Exception.Flatten().InnerExceptions)
            {
                if (exception.StackTrace?.Contains("System.Net.Sockets", StringComparison.Ordinal) == true)
                {
                    unobserved.Enqueue(exception);
                }
            }
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await ExerciseUdpAssociationsAsync(termination);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(10);
            }
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ExerciseUdpAssociationsAsync(string termination)
    {
        var endpoint = new LocalSocks5VpnEndpoint(
            new TunnelDiagnostics(), NullLogger<LocalSocks5VpnEndpoint>.Instance);
        endpoint.Start(
            new VpnAdapterInfo("127.0.0.1", (uint)NetworkInterface.LoopbackInterfaceIndex, 0, "127.0.0.1"),
            new TunnelDnsSettings(TunnelDnsSettings.GOOGLE_PUBLIC_DNS));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var echoServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var datagrams = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var controls = new List<TcpClient>();

        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var (control, relay) = await OpenUdpAssociationAsync(endpoint.Port, timeout.Token);
                controls.Add(control);
                await ExchangeDatagramAsync(datagrams, echoServer, relay, timeout.Token);
                if (termination == "close") control.Dispose();
                if (termination == "reset")
                {
                    control.Client.LingerState = new LingerOption(true, 0);
                    control.Dispose();
                }
            }
        }
        finally
        {
            await endpoint.StopAsync().WaitAsync(timeout.Token);
            foreach (var control in controls) control.Dispose();
        }
    }

    private static async Task<(TcpClient Control, IPEndPoint Relay)> OpenUdpAssociationAsync(
        int port, CancellationToken cancellationToken)
    {
        var control = new TcpClient();
        try
        {
            await control.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            var stream = control.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
            var negotiation = new byte[2];
            await stream.ReadExactlyAsync(negotiation, cancellationToken);
            Assert.Equal(new byte[] { 5, 0 }, negotiation);
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken);
            var response = new byte[10];
            await stream.ReadExactlyAsync(response, cancellationToken);
            Assert.Equal((byte)0, response[1]);
            var relay = new IPEndPoint(new IPAddress(response.AsSpan(4, 4)),
                BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8, 2)));
            return (control, relay);
        }
        catch
        {
            control.Dispose();
            throw;
        }
    }

    private static async Task ExchangeDatagramAsync(
        UdpClient client, UdpClient server, IPEndPoint relay, CancellationToken cancellationToken)
    {
        var destination = (IPEndPoint)server.Client.LocalEndPoint!;
        byte[] payload = [1, 2, 3, 4];
        var request = new byte[10 + payload.Length];
        request[3] = 1;
        destination.Address.GetAddressBytes().CopyTo(request, 4);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), (ushort)destination.Port);
        payload.CopyTo(request, 10);
        await client.SendAsync(request, relay, cancellationToken);
        var received = await server.ReceiveAsync(cancellationToken);
        Assert.Equal(payload, received.Buffer);
        await server.SendAsync(received.Buffer, received.RemoteEndPoint, cancellationToken);
        var response = await client.ReceiveAsync(cancellationToken);
        Assert.Equal(request, response.Buffer);
    }

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

[CollectionDefinition("SOCKS5 socket lifetime", DisableParallelization = true)]
public sealed class Socks5SocketLifetimeCollection { }
