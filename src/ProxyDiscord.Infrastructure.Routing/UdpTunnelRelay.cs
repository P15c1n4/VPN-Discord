using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class UdpTunnelRelay(
    FlowRegistry flows,
    TunnelDiagnostics diagnostics,
    ILogger<UdpTunnelRelay> logger) : IAsyncDisposable
{
    private static readonly TimeSpan SESSION_IDLE_TIMEOUT = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<UdpSessionKey, UdpSession> _sessions = new();
    private readonly LocalAddressSet _localAddresses = new();

    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private VpnAdapterInfo? _vpnAdapter;
    private IPAddress? _dnsServer;

    public int ListenPort { get; private set; }

    public event EventHandler? TrafficRelayed;

    public void Start(VpnAdapterInfo vpnAdapter, TunnelDnsSettings dns)
    {
        _vpnAdapter = vpnAdapter;
        _dnsServer = IPAddress.TryParse(dns.ServerIp, out var parsed)
            ? parsed
            : IPAddress.Parse(TunnelDnsSettings.GOOGLE_PUBLIC_DNS);
        _cts = new CancellationTokenSource();

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _listener.Bind(new IPEndPoint(IPAddress.Any, 0));
        ListenPort = ((IPEndPoint)_listener.LocalEndPoint!).Port;

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        logger.LogInformation(
            "Relay UDP ouvindo em 0.0.0.0:{Port} (DNS do túnel {Dns}, saída pela interface VPN {IfIdx})",
            ListenPort, _dnsServer, vpnAdapter.InterfaceIndex);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65535];

        while (!cancellationToken.IsCancellationRequested && _listener is { } listener)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await listener.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Falha ao receber datagrama no relay UDP");
                continue;
            }

            if (received.RemoteEndPoint is not IPEndPoint from || !_localAddresses.IsLocal(from.Address))
            {
                continue;
            }

            var payload = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
            _ = ForwardAsync(from, payload, cancellationToken);
        }
    }

    private async Task ForwardAsync(IPEndPoint from, byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            if (!flows.TryGetDestination(TransportProtocol.Udp, from.Port, out var flow) ||
                flow.RemoteIp is not { } remoteIp)
            {
                return;
            }

            var destination = ResolveDestination(remoteIp, flow.RemotePort);
            var key = new UdpSessionKey(from.Port, destination.Address.ToString(), destination.Port);

            var session = GetOrCreateSession(key, from, destination, cancellationToken);
            if (session is null)
            {
                return;
            }

            session.LastActivityUtc = DateTime.UtcNow;
            await session.Upstream.SendToAsync(payload, SocketFlags.None, destination, cancellationToken);
            diagnostics.BytesRelayed(TransportProtocol.Udp, payload.Length, 0);
            TrafficRelayed?.Invoke(this, EventArgs.Empty);
        }
        catch (SocketException ex)
        {
            diagnostics.UpstreamFailed(TransportProtocol.Udp, "?", ex.SocketErrorCode.ToString());
            logger.LogWarning(
                "Falha ao enviar datagrama UDP pela VPN: {Error}. {Hint}",
                ex.SocketErrorCode,
                ex.SocketErrorCode == SocketError.NetworkUnreachable
                    ? "A interface VPN não tem rota para esse destino — verifique a rota do túnel."
                    : string.Empty);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao encaminhar datagrama UDP pelo túnel");
        }
    }

    private UdpSession? GetOrCreateSession(
        UdpSessionKey key, IPEndPoint from, IPEndPoint destination, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        Socket upstream;
        try
        {
            upstream = VpnBoundSocketFactory.CreateUdpSocket(_vpnAdapter!);
        }
        catch (SocketException ex)
        {
            diagnostics.UpstreamFailed(TransportProtocol.Udp, destination.ToString(), ex.SocketErrorCode.ToString());
            logger.LogWarning(ex, "Não foi possível abrir socket UDP fixado na VPN para {Destination}", destination);
            return null;
        }

        var candidate = new UdpSession(upstream) { LastActivityUtc = DateTime.UtcNow };
        var winner = _sessions.GetOrAdd(key, candidate);

        if (!ReferenceEquals(winner, candidate))
        {
            upstream.Dispose();
            return winner;
        }

        diagnostics.UpstreamConnected(
            TransportProtocol.Udp, destination.ToString(), upstream.LocalEndPoint?.ToString() ?? "?");
        logger.LogDebug(
            "Túnel UDP: porta local {Port} -> {Destination} por {LocalEndpoint}",
            from.Port, destination, upstream.LocalEndPoint);

        _ = PumpRepliesAsync(candidate, key, from, cancellationToken);
        return candidate;
    }

    private IPEndPoint ResolveDestination(string remoteIp, int remotePort) =>
        remotePort == 53 && _dnsServer is not null
            ? new IPEndPoint(_dnsServer, 53)
            : new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);

    private async Task PumpRepliesAsync(
        UdpSession session, UdpSessionKey key, IPEndPoint replyTo, CancellationToken cancellationToken)
    {
        var buffer = new byte[65535];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await session.Upstream.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);

                session.LastActivityUtc = DateTime.UtcNow;

                if (_listener is { } listener)
                {
                    await listener.SendToAsync(
                        buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, replyTo, cancellationToken);
                    diagnostics.BytesRelayed(TransportProtocol.Udp, 0, received.ReceivedBytes);
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao devolver resposta UDP para a porta {Port}", replyTo.Port);
        }
        finally
        {
            RemoveSession(key, session);
        }
    }

    public void ExpireIdleSessions(DateTime nowUtc)
    {
        foreach (var (key, session) in _sessions)
        {
            if (nowUtc - session.LastActivityUtc >= SESSION_IDLE_TIMEOUT)
            {
                RemoveSession(key, session);
            }
        }
    }

    private void RemoveSession(UdpSessionKey key, UdpSession session)
    {
        if (((ICollection<KeyValuePair<UdpSessionKey, UdpSession>>)_sessions)
            .Remove(new KeyValuePair<UdpSessionKey, UdpSession>(key, session)))
        {
            session.Upstream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        _listener?.Dispose();
        _listener = null;

        foreach (var (_, session) in _sessions)
        {
            session.Upstream.Dispose();
        }

        _sessions.Clear();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop;
            }
            catch (OperationCanceledException)
            {
            }

            _receiveLoop = null;
        }

        _cts.Dispose();
        _cts = null;
    }

    private readonly record struct UdpSessionKey(int SourcePort, string DestinationIp, int DestinationPort);

    private sealed class UdpSession(Socket upstream)
    {
        public Socket Upstream { get; } = upstream;
        public DateTime LastActivityUtc { get; set; }
    }
}
