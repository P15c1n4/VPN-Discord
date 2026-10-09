using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

internal sealed class LocalSocks5VpnEndpoint(TunnelDiagnostics diagnostics, ILogger<LocalSocks5VpnEndpoint> logger)
{
    private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
    private readonly ConcurrentDictionary<Guid, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<Guid, Task> _handlers = new();
    private TcpListener? _listener;
    private VpnAdapterInfo? _adapter;
    private IPAddress? _dnsServer;
    private CancellationTokenSource? _stop;
    private Task? _acceptLoop;

    public event EventHandler? TrafficObserved;
    public int Port { get; private set; }

    public void Start(VpnAdapterInfo adapter, TunnelDnsSettings dnsSettings)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("O endpoint SOCKS5 já está ativo.");
        }

        _adapter = adapter;
        _dnsServer = IPAddress.TryParse(dnsSettings.ServerIp, out var dnsServer)
            ? dnsServer
            : IPAddress.Parse(TunnelDnsSettings.GOOGLE_PUBLIC_DNS);
        _stop = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    public async Task StopAsync()
    {
        _stop?.Cancel();
        _listener?.Stop();
        _listener = null;

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        try { await Task.WhenAll(_handlers.Values.ToArray()); }
        catch (Exception ex) { logger.LogWarning(ex, "Falha ao encerrar conexões do endpoint SOCKS5"); }

        _clients.Clear();
        _handlers.Clear();
        _stop?.Dispose();
        _stop = null;
        _acceptLoop = null;
        _adapter = null;
        _dnsServer = null;
        Port = 0;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { } listener)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { return; }

            var id = Guid.NewGuid();
            _clients[id] = client;
            var handler = HandleClientAsync(id, client, cancellationToken);
            _handlers[id] = handler;
            _ = handler.ContinueWith(
                _ => RemoveHandler(id),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(Guid id, TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                if (!await NegotiateAsync(stream, cancellationToken))
                {
                    return;
                }

                var request = await ReadRequestAsync(stream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                if (request.Value.Command == 1)
                {
                    await HandleTcpConnectAsync(stream, request.Value, cancellationToken);
                }
                else if (request.Value.Command == 3)
                {
                    await HandleUdpAssociateAsync(stream, cancellationToken);
                }
                else
                {
                    await WriteReplyAsync(stream, 7, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.Note($"Conexão SOCKS5 encerrada: {ex.Message}", DiagnosticSeverity.Warning);
            }
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    private static async Task<bool> NegotiateAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        if (!await ReadExactlyAsync(stream, header, cancellationToken) || header[0] != 5)
        {
            return false;
        }

        var methods = new byte[header[1]];
        if (!await ReadExactlyAsync(stream, methods, cancellationToken))
        {
            return false;
        }

        if (!methods.Contains((byte)0))
        {
            await stream.WriteAsync(new byte[] { 5, 0xFF }, cancellationToken);
            return false;
        }

        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken);
        return true;
    }

    private static async Task<SocksRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellationToken) || header[0] != 5 || header[2] != 0)
        {
            return null;
        }

        IPAddress? address = null;
        switch (header[3])
        {
            case 1:
                var ipv4 = new byte[4];
                if (!await ReadExactlyAsync(stream, ipv4, cancellationToken)) return null;
                address = new IPAddress(ipv4);
                break;
            case 4:
                var ipv6 = new byte[16];
                if (!await ReadExactlyAsync(stream, ipv6, cancellationToken)) return null;
                address = new IPAddress(ipv6);
                break;
            case 3:
                var length = new byte[1];
                if (!await ReadExactlyAsync(stream, length, cancellationToken)) return null;
                var domain = new byte[length[0]];
                if (!await ReadExactlyAsync(stream, domain, cancellationToken)) return null;
                break;
            default:
                return null;
        }

        var portBytes = new byte[2];
        if (!await ReadExactlyAsync(stream, portBytes, cancellationToken)) return null;
        return new SocksRequest(header[1], address, BinaryPrimitives.ReadUInt16BigEndian(portBytes));
    }

    private async Task HandleTcpConnectAsync(
        NetworkStream clientStream,
        SocksRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Address is null || request.Address.AddressFamily != AddressFamily.InterNetwork || request.Port == 0)
        {
            await WriteReplyAsync(clientStream, 8, cancellationToken);
            return;
        }

        using var upstream = VpnBoundSocketFactory.CreateTcpSocket(_adapter!);
        try
        {
            await upstream.ConnectAsync(new IPEndPoint(request.Address, request.Port), cancellationToken);
        }
        catch (SocketException ex)
        {
            diagnostics.UpstreamFailed(TransportProtocol.Tcp, $"{request.Address}:{request.Port}", ex.Message);
            await WriteReplyAsync(clientStream, 5, cancellationToken);
            return;
        }

        var localEndpoint = (IPEndPoint)upstream.LocalEndPoint!;
        await WriteReplyAsync(clientStream, 0, cancellationToken, localEndpoint.Port, localEndpoint.Address);
        diagnostics.UpstreamConnected(TransportProtocol.Tcp, $"{request.Address}:{request.Port}", "SOCKS5");
        TrafficObserved?.Invoke(this, EventArgs.Empty);

        using var upstreamStream = new NetworkStream(upstream, ownsSocket: false);
        using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var upload = CopyAsync(clientStream, upstreamStream, true, relayStop.Token);
        var download = CopyAsync(upstreamStream, clientStream, false, relayStop.Token);
        await Task.WhenAny(upload, download);
        relayStop.Cancel();
        try { await Task.WhenAll(upload, download); }
        catch (Exception) { }
    }

    private async Task CopyAsync(
        NetworkStream source,
        NetworkStream destination,
        bool upstream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            diagnostics.BytesRelayed(TransportProtocol.Tcp, upstream ? count : 0, upstream ? 0 : count);
        }
    }

    private async Task HandleUdpAssociateAsync(NetworkStream controlStream, CancellationToken cancellationToken)
    {
        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        DisableUdpConnectionReset(clientSocket);
        clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var relayPort = ((IPEndPoint)clientSocket.LocalEndPoint!).Port;
        await WriteReplyAsync(controlStream, 0, cancellationToken, relayPort);

        using var vpnSocket = VpnBoundSocketFactory.CreateUdpSocket(_adapter!);
        DisableUdpConnectionReset(vpnSocket);
        using var association = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var controlClosed = WatchControlConnectionAsync(controlStream, association.Token);
        var clientBuffer = new byte[65535];
        var remoteBuffer = new byte[65535];
        EndPoint clientTemplate = new IPEndPoint(IPAddress.Any, 0);
        EndPoint remoteTemplate = new IPEndPoint(IPAddress.Any, 0);
        Task<SocketReceiveFromResult> clientReceive = clientSocket.ReceiveFromAsync(clientBuffer, SocketFlags.None, clientTemplate, association.Token).AsTask();
        Task<SocketReceiveFromResult> remoteReceive = vpnSocket.ReceiveFromAsync(remoteBuffer, SocketFlags.None, remoteTemplate, association.Token).AsTask();
        IPEndPoint? associatedClient = null;
        var destinations = new HashSet<IPEndPoint>();
        var dnsQueries = new Dictionary<ushort, DnsQueryRoute>();

        try
        {
            while (!association.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(clientReceive, remoteReceive, controlClosed);
                if (completed == controlClosed) return;

                if (completed == clientReceive)
                {
                    var packet = await clientReceive;
                    if (packet.RemoteEndPoint is IPEndPoint clientEndpoint &&
                        TryParseUdpRequest(clientBuffer, packet.ReceivedBytes, out var destination, out var payloadOffset))
                    {
                        associatedClient = clientEndpoint;
                        try
                        {
                            var originalDestination = destination;
                            if (destination.Port == 53 && _dnsServer is not null &&
                                packet.ReceivedBytes - payloadOffset >= 2)
                            {
                                var transactionId = BinaryPrimitives.ReadUInt16BigEndian(clientBuffer.AsSpan(payloadOffset, 2));
                                dnsQueries[transactionId] = new DnsQueryRoute(clientEndpoint, originalDestination);
                                destination = new IPEndPoint(_dnsServer, 53);
                            }

                            await vpnSocket.SendToAsync(clientBuffer.AsMemory(payloadOffset, packet.ReceivedBytes - payloadOffset),
                                SocketFlags.None, destination, association.Token);
                            TrafficObserved?.Invoke(this, EventArgs.Empty);
                            if (destinations.Add(destination))
                            {
                                diagnostics.UpstreamConnected(TransportProtocol.Udp, destination.ToString(), "SOCKS5");
                            }

                            diagnostics.BytesRelayed(TransportProtocol.Udp, packet.ReceivedBytes - payloadOffset, 0);
                        }
                        catch (SocketException ex)
                        {
                            diagnostics.UpstreamFailed(TransportProtocol.Udp, destination.ToString(), ex.Message);
                        }
                    }

                    clientReceive = clientSocket.ReceiveFromAsync(clientBuffer, SocketFlags.None, clientTemplate, association.Token).AsTask();
                }
                else
                {
                    var packet = await remoteReceive;
                    if (packet.RemoteEndPoint is IPEndPoint remoteEndpoint)
                    {
                        var replyTo = associatedClient;
                        var replySource = remoteEndpoint;
                        if (remoteEndpoint.Port == 53 && packet.ReceivedBytes >= 2)
                        {
                            var transactionId = BinaryPrimitives.ReadUInt16BigEndian(remoteBuffer.AsSpan(0, 2));
                            if (dnsQueries.Remove(transactionId, out var dnsRoute))
                            {
                                replyTo = dnsRoute.Client;
                                replySource = dnsRoute.OriginalDestination;
                            }
                        }

                        if (replyTo is not null)
                        {
                            var response = CreateUdpResponse(remoteBuffer, packet.ReceivedBytes, replySource);
                            await clientSocket.SendToAsync(response, SocketFlags.None, replyTo, association.Token);
                            diagnostics.BytesRelayed(TransportProtocol.Udp, 0, packet.ReceivedBytes);
                        }
                    }

                    remoteReceive = vpnSocket.ReceiveFromAsync(remoteBuffer, SocketFlags.None, remoteTemplate, association.Token).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (association.IsCancellationRequested) { }
        catch (SocketException) when (association.IsCancellationRequested) { }
        finally
        {
            association.Cancel();
            try { await controlClosed; } catch (Exception) { }
        }
    }

    private static async Task WatchControlConnectionAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, cancellationToken) > 0) { }
    }

    private static void DisableUdpConnectionReset(Socket socket)
    {
        try { socket.IOControl(SIO_UDP_CONNRESET, new byte[sizeof(int)], null); }
        catch (Exception ex) when (ex is SocketException or NotSupportedException) { }
    }

    private static bool TryParseUdpRequest(
        byte[] packet,
        int length,
        out IPEndPoint destination,
        out int payloadOffset)
    {
        destination = new IPEndPoint(IPAddress.Any, 0);
        payloadOffset = 0;
        if (length < 10 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0 || packet[3] != 1)
        {
            return false;
        }

        var address = new IPAddress(packet.AsSpan(4, 4));
        var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2));
        if (port == 0) return false;
        destination = new IPEndPoint(address, port);
        payloadOffset = 10;
        return length > payloadOffset;
    }

    private static byte[] CreateUdpResponse(byte[] payload, int length, IPEndPoint remoteEndpoint)
    {
        if (remoteEndpoint.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            return [];
        }

        var response = new byte[10 + length];
        response[3] = 1;
        remoteEndpoint.Address.GetAddressBytes().CopyTo(response, 4);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(8, 2), (ushort)remoteEndpoint.Port);
        payload.AsSpan(0, length).CopyTo(response.AsSpan(10));
        return response;
    }

    private static Task WriteReplyAsync(
        NetworkStream stream,
        byte status,
        CancellationToken cancellationToken,
        int port = 0,
        IPAddress? boundAddress = null)
    {
        var response = new byte[10];
        response[0] = 5;
        response[1] = status;
        response[3] = 1;
        (boundAddress ?? IPAddress.Loopback).GetAddressBytes().CopyTo(response, 4);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(8, 2), (ushort)port);
        return stream.WriteAsync(response, cancellationToken).AsTask();
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private void RemoveHandler(Guid id) => _handlers.TryRemove(id, out _);

    private sealed record DnsQueryRoute(IPEndPoint Client, IPEndPoint OriginalDestination);
    private readonly record struct SocksRequest(byte Command, IPAddress? Address, int Port);
}
