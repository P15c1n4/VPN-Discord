using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class TcpTunnelRelay(FlowRegistry flows, TunnelDiagnostics diagnostics, ILogger<TcpTunnelRelay> logger)
    : IAsyncDisposable
{
    // Quanto o teardown espera as sessões em andamento terminarem. Elas são derrubadas antes
    // (os sockets são fechados), então isto é só a margem para os laços desenrolarem.
    private static readonly TimeSpan SESSION_DRAIN_TIMEOUT = TimeSpan.FromSeconds(3);

    private readonly LocalAddressSet _localAddresses = new();
    private readonly ConcurrentDictionary<long, TcpSession> _sessions = new();

    private long _nextSessionId;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private VpnAdapterInfo? _vpnAdapter;

    public int ListenPort { get; private set; }

    public event EventHandler? TrafficRelayed;

    public void Start(VpnAdapterInfo vpnAdapter)
    {
        _vpnAdapter = vpnAdapter;
        _cts = new CancellationTokenSource();

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Any, 0));
        _listener.Listen(128);
        ListenPort = ((IPEndPoint)_listener.LocalEndPoint!).Port;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        logger.LogInformation(
            "Relay TCP ouvindo em 0.0.0.0:{Port} (saída pela interface VPN {IfIdx}, origem {VpnIp})",
            ListenPort, vpnAdapter.InterfaceIndex, vpnAdapter.LocalIp);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { } listener)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Falha ao aceitar conexão no relay");
                continue;
            }

            if (client.RemoteEndPoint is not IPEndPoint remote || !_localAddresses.IsLocal(remote.Address))
            {
                logger.LogWarning("Conexão externa recusada no relay: {Remote}", client.RemoteEndPoint);
                client.Dispose();
                continue;
            }

            var id = Interlocked.Increment(ref _nextSessionId);
            var session = new TcpSession(client);
            _sessions[id] = session;
            session.Work = RunSessionAsync(id, session, remote.Port, cancellationToken);
        }
    }

    private async Task RunSessionAsync(long id, TcpSession session, int sourcePort, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(session, sourcePort, cancellationToken);
        }
        finally
        {
            _sessions.TryRemove(id, out _);
        }
    }

    private async Task HandleClientAsync(TcpSession session, int sourcePort, CancellationToken cancellationToken)
    {
        var client = session.Client;
        Socket? upstream = null;
        IPEndPoint? destination = null;
        try
        {
            if (!flows.TryGetDestination(TransportProtocol.Tcp, sourcePort, out var flow) ||
                flow.RemoteIp is not { } remoteIp)
            {
                logger.LogWarning(
                    "Conexão no relay da porta {Port} sem destino original conhecido; descartando.", sourcePort);
                client.Dispose();
                return;
            }

            destination = new IPEndPoint(IPAddress.Parse(remoteIp), flow.RemotePort);
            upstream = VpnBoundSocketFactory.CreateTcpSocket(_vpnAdapter!);
            session.Upstream = upstream;

            try
            {
                await upstream.ConnectAsync(destination, cancellationToken);
            }
            catch (SocketException ex)
            {
                diagnostics.UpstreamFailed(TransportProtocol.Tcp, destination.ToString(), ex.SocketErrorCode.ToString());
                logger.LogWarning(
                    "Falha ao abrir a conexão TCP para {Destination} pela VPN: {Error}. {Hint}",
                    destination, ex.SocketErrorCode,
                    ex.SocketErrorCode == SocketError.NetworkUnreachable
                        ? "A interface VPN não tem rota para esse destino — verifique a rota do túnel."
                        : string.Empty);
                client.Dispose();
                upstream.Dispose();
                return;
            }

            diagnostics.UpstreamConnected(
                TransportProtocol.Tcp, destination.ToString(), upstream.LocalEndPoint?.ToString() ?? "?");
            TrafficRelayed?.Invoke(this, EventArgs.Empty);
            logger.LogDebug(
                "Túnel TCP: porta local {Port} -> {Destination} por {LocalEndpoint}",
                sourcePort, destination, upstream.LocalEndPoint);

            var toUpstream = PumpAsync(client, upstream, TransportProtocol.Tcp, upstreamDirection: true, cancellationToken);
            var toClient = PumpAsync(upstream, client, TransportProtocol.Tcp, upstreamDirection: false, cancellationToken);
            await Task.WhenAll(toUpstream, toClient);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Conexão tunelada encerrada");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha inesperada ao tunelar conexão TCP para {Destination}", destination);
        }
        finally
        {
            client.Dispose();
            upstream?.Dispose();
        }
    }

    private async Task PumpAsync(
        Socket from, Socket to, TransportProtocol protocol, bool upstreamDirection, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                if (read == 0)
                {
                    TryShutdownSend(to);
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, cancellationToken);
                diagnostics.BytesRelayed(
                    protocol,
                    upstreamDirection ? read : 0,
                    upstreamDirection ? 0 : read);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            TryShutdownSend(to);
        }
    }

    private static void TryShutdownSend(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
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

        await CloseLiveSessionsAsync();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }

            _acceptLoop = null;
        }

        _cts.Dispose();
        _cts = null;
    }

    // O cancelamento sozinho não fecha nada: cada pump só repara nele no próximo await, e o
    // desconectar segue derrubando rota e VPN por baixo de conexões ainda abertas. Fechar os
    // sockets aqui faz os laços caírem na hora, e esperá-los é o que permite dizer que, quando o
    // Dispose retorna, não sobrou conexão nenhuma pela VPN.
    private async Task CloseLiveSessionsAsync()
    {
        var live = _sessions.Values.ToArray();
        _sessions.Clear();

        if (live.Length == 0)
        {
            return;
        }

        foreach (var session in live)
        {
            session.CloseSockets();
        }

        var pending = live.Select(session => session.Work).OfType<Task>().ToArray();

        try
        {
            await Task.WhenAll(pending).WaitAsync(SESSION_DRAIN_TIMEOUT);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "{Count} sessão(ões) do relay TCP não encerraram em {Seconds}s.",
                pending.Length, SESSION_DRAIN_TIMEOUT.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sessão do relay TCP encerrada com erro durante a parada");
        }

        logger.LogInformation("Relay TCP parado; {Count} conexão(ões) em andamento fechadas.", live.Length);
    }

    private sealed class TcpSession(Socket client)
    {
        public Socket Client { get; } = client;

        public Socket? Upstream { get; set; }

        public Task? Work { get; set; }

        public void CloseSockets()
        {
            Close(Client);
            Close(Upstream);
        }

        private static void Close(Socket? socket)
        {
            try
            {
                socket?.Dispose();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }
    }
}
