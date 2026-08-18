using System.Net;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class ProcessRoutingEngine(
    IWinDivertHandleFactory handleFactory,
    FlowRegistry flows,
    IProcessGroupWatcher processWatcher,
    TcpTunnelRelay tcpRelay,
    UdpTunnelRelay udpRelay,
    TunnelDiagnostics diagnostics,
    ISystemClock clock,
    ILogger<ProcessRoutingEngine> logger) : IProcessRoutingEngine
{
    public static string CaptureFilterFor(TunnelProtocolScope scope) => scope switch
    {
        TunnelProtocolScope.TcpOnly => "outbound and tcp",
        TunnelProtocolScope.UdpOnly => "outbound and udp",
        _ => "outbound and (tcp or udp)",
    };

    public const string SOCKET_EVENT_FILTER =
        "event == BIND or event == CONNECT or event == ACCEPT or event == CLOSE";

    private static readonly TimeSpan EXPIRY_SWEEP_INTERVAL = TimeSpan.FromSeconds(30);

    private const int NO_RELAY_PORT = -1;

    private readonly int _ownProcessId = Environment.ProcessId;

    private IWinDivertHandle? _handle;
    private IWinDivertSocketEvents? _socketEvents;
    private Task? _captureLoopTask;
    private Task? _socketLoopTask;
    private volatile bool _running;
    private int _relayPort;
    private int _udpRelayPort;
    private TunnelProtocolScope _scope = TunnelProtocolScope.TcpAndUdp;

    public bool IsRunning => _running;

    public event EventHandler? TrafficObserved;

    public async Task StartAsync(
        TargetProcessSelector target,
        VpnAdapterInfo vpnAdapter,
        TunnelDnsSettings dnsSettings,
        TunnelProtocolScope scope = TunnelProtocolScope.TcpAndUdp,
        CancellationToken cancellationToken = default)
    {
        if (_running)
        {
            throw new InvalidOperationException("O motor de roteamento já está em execução.");
        }

        _scope = scope;
        diagnostics.Reset();
        diagnostics.SetScope(scope);

        flows.Clear();
        var seeded = flows.SeedFromIpHelper(clock.UtcNow);
        logger.LogInformation(
            "Registro de fluxos semeado com {Count} portas já em uso (a camada SOCKET não enxerga eventos anteriores à abertura do handle).",
            seeded);

        _relayPort = NO_RELAY_PORT;
        _udpRelayPort = NO_RELAY_PORT;

        try
        {
            processWatcher.Start(target);

            if (scope.Includes(TransportProtocol.Tcp))
            {
                tcpRelay.TrafficRelayed += OnTrafficRelayed;
                tcpRelay.Start(vpnAdapter);
                _relayPort = tcpRelay.ListenPort;
            }

            if (scope.Includes(TransportProtocol.Udp))
            {
                udpRelay.TrafficRelayed += OnTrafficRelayed;
                udpRelay.Start(vpnAdapter, dnsSettings);
                _udpRelayPort = udpRelay.ListenPort;
            }

            if (_relayPort == _udpRelayPort)
            {
                throw new InvalidOperationException(
                    $"Os relays TCP e UDP receberam a mesma porta ({_relayPort}); impossível distinguir os fluxos.");
            }

            _socketEvents = handleFactory.OpenSocketEvents(SOCKET_EVENT_FILTER);
            _handle = handleFactory.OpenNetwork(CaptureFilterFor(scope));
            _running = true;

            _socketLoopTask = Task.Run(SocketEventLoop, CancellationToken.None);
            _captureLoopTask = Task.Run(CaptureLoop, CancellationToken.None);
        }
        catch
        {
            // Um Start que falha no meio já subiu relay, watcher ou handle. Sem isto eles ficariam
            // vivos com _running == false — invisíveis para o StopAsync e vazados de vez, já que a
            // tentativa seguinte sobrescreveria os campos e perderia os sockets antigos.
            await TearDownAsync();
            throw;
        }

        logger.LogInformation(
            "Motor de roteamento iniciado para '{Target}' via interface VPN {IfIdx} ({VpnIp}); escopo {Scope}, relay TCP {Tcp}, relay UDP {Udp}",
            target.DisplayName, vpnAdapter.InterfaceIndex, vpnAdapter.LocalIp, scope, DescribePort(_relayPort), DescribePort(_udpRelayPort));
        diagnostics.Note(
            $"Alvo {target.DisplayName} · VPN if {vpnAdapter.InterfaceIndex} ({vpnAdapter.LocalIp}) · " +
            $"relay TCP {DescribePort(_relayPort)} · UDP {DescribePort(_udpRelayPort)}");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_running)
        {
            return;
        }

        await TearDownAsync();

        logger.LogInformation("Motor de roteamento parado. {Report}", diagnostics.BuildReport());
    }

    // Desfaz tudo o que o Start levanta, na ordem inversa, e é seguro rodar sobre um estado
    // parcial: cada passo checa o que existe. Desinscrever e dar Dispose nos dois relays é
    // incondicional de propósito — o relay fora de escopo nunca foi iniciado e o Dispose dele
    // retorna na hora, enquanto uma desinscrição condicional deixaria handler pendurado se o
    // Start tivesse falhado entre o += e o Start do relay.
    private async Task TearDownAsync()
    {
        _running = false;

        _handle?.Dispose();
        _handle = null;
        _socketEvents?.Dispose();
        _socketEvents = null;

        foreach (var task in new[] { _captureLoopTask, _socketLoopTask })
        {
            if (task is not null)
            {
                await task;
            }
        }

        _captureLoopTask = null;
        _socketLoopTask = null;

        tcpRelay.TrafficRelayed -= OnTrafficRelayed;
        udpRelay.TrafficRelayed -= OnTrafficRelayed;
        await tcpRelay.DisposeAsync();
        await udpRelay.DisposeAsync();

        _relayPort = NO_RELAY_PORT;
        _udpRelayPort = NO_RELAY_PORT;

        processWatcher.Dispose();
        flows.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void OnTrafficRelayed(object? sender, EventArgs e) => TrafficObserved?.Invoke(this, EventArgs.Empty);

    private static string DescribePort(int port) => port == NO_RELAY_PORT ? "fora do escopo" : port.ToString();

    private void SocketEventLoop()
    {
        while (_running && _socketEvents is { } events)
        {
            SocketEvent socketEvent;
            int win32Error;
            try
            {
                if (!events.TryReceive(out socketEvent, out win32Error))
                {
                    ReportLoopExit("camada SOCKET", win32Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    logger.LogError(ex, "Erro ao receber evento de socket; identificação de processo degradada");
                }

                return;
            }

            diagnostics.SocketEventCaptured();

            try
            {
                flows.Apply(socketEvent, clock.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao registrar evento de socket");
            }
        }
    }

    private void CaptureLoop()
    {
        var buffer = new byte[ushort.MaxValue];
        var lastSweep = clock.UtcNow;

        while (_running && _handle is { } handle)
        {
            bool received;
            int length;
            PacketAddress address;
            int win32Error;
            try
            {
                received = handle.TryReceive(buffer, out length, out address, out win32Error);
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    logger.LogError(ex, "Erro ao capturar pacote; motor de roteamento será interrompido");
                }

                MarkStopped();
                return;
            }

            if (!received)
            {
                ReportLoopExit("camada NETWORK", win32Error);
                MarkStopped();
                return;
            }

            diagnostics.PacketCaptured();

            try
            {
                ProcessPacket(handle, buffer, length, address);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar pacote capturado; pacote descartado");
            }

            var now = clock.UtcNow;
            if (now - lastSweep >= EXPIRY_SWEEP_INTERVAL)
            {
                flows.ExpireStale(now);
                udpRelay.ExpireIdleSessions(now);
                lastSweep = now;
            }
        }
    }

    private void MarkStopped() => _running = false;

    private void ReportLoopExit(string stage, int win32Error)
    {
        if (!_running)
        {
            return;
        }

        logger.LogError("Captura na {Stage} terminou inesperadamente (erro Win32 {Error}).", stage, win32Error);
        diagnostics.CaptureFailed(win32Error, stage);
    }

    private void ProcessPacket(IWinDivertHandle handle, byte[] buffer, int length, in PacketAddress address)
    {
        var span = buffer.AsSpan(0, length);

        if (address.IsIpv6)
        {
            HandleIpv6(handle, span, address);
            return;
        }

        var flow = PacketRewriter.Parse(span);
        if (!flow.IsValid)
        {
            Forward(handle, span, address, "pass-through");
            return;
        }

        if (!_scope.Includes(flow.Protocol))
        {
            Forward(handle, span, address, "fora-do-escopo");
            return;
        }

        var relayPort = flow.Protocol == TransportProtocol.Tcp ? _relayPort : _udpRelayPort;

        if (flow.SrcPort == relayPort &&
            flows.TryGetDestination(flow.Protocol, flow.DstPort, out var known) &&
            known.RemoteIp is { } knownRemote)
        {
            RedirectPacketRewriter.RestoreFromRelay(
                span,
                new RedirectedFlow(flow.Protocol, flow.DstIp, flow.DstPort, knownRemote, known.RemotePort));
            Forward(handle, span, address, "resposta-do-relay");
            return;
        }

        if (flow.DstPort == relayPort)
        {
            Forward(handle, span, address, "pass-through");
            return;
        }

        if (!flows.TryGetOwner(flow.Protocol, flow.SrcPort, out var ownerPid, out var fromSocketLayer))
        {
            diagnostics.OwnerUnresolved();
            Forward(handle, span, address, "sem-dono");
            return;
        }

        diagnostics.OwnerResolved(fromSocketLayer);

        if (ownerPid == _ownProcessId || !processWatcher.IsTracked(ownerPid))
        {
            diagnostics.NotTargetProcess();
            Forward(handle, span, address, "pass-through");
            return;
        }

        diagnostics.TargetMatched(flow.Protocol);

        flows.RecordDestinationFromPacket(
            flow.Protocol, flow.SrcPort, ownerPid, flow.DstIp, flow.DstPort, clock.UtcNow);

        RedirectPacketRewriter.RedirectToRelay(span, flow.SrcIp, relayPort);
        diagnostics.PacketRedirected(flow.Protocol, $"{flow.DstIp}:{flow.DstPort}");

        Forward(handle, span, address.AsInbound(), "app-para-relay");
    }

    private void HandleIpv6(IWinDivertHandle handle, Span<byte> span, in PacketAddress address)
    {
        if (!TryParseIpv6Ports(span, out var protocol, out var srcPort))
        {
            Forward(handle, span, address, "pass-through");
            return;
        }

        if (!_scope.Includes(protocol) ||
            !flows.TryGetOwner(protocol, srcPort, out var ownerPid, out _) ||
            ownerPid == _ownProcessId ||
            !processWatcher.IsTracked(ownerPid))
        {
            Forward(handle, span, address, "pass-through");
            return;
        }

        diagnostics.Ipv6Blocked(protocol, (ushort)srcPort);
    }

    private static bool TryParseIpv6Ports(ReadOnlySpan<byte> packet, out TransportProtocol protocol, out int srcPort)
    {
        protocol = default;
        srcPort = 0;

        const int IPV6_HEADER_LENGTH = 40;
        if (packet.Length < IPV6_HEADER_LENGTH + 4 || (packet[0] >> 4) != 6)
        {
            return false;
        }

        var nextHeader = packet[6];
        if (nextHeader != (byte)TransportProtocol.Tcp && nextHeader != (byte)TransportProtocol.Udp)
        {
            return false;
        }

        protocol = (TransportProtocol)nextHeader;
        srcPort = (packet[IPV6_HEADER_LENGTH] << 8) | packet[IPV6_HEADER_LENGTH + 1];
        return true;
    }

    private void Forward(IWinDivertHandle handle, ReadOnlySpan<byte> span, in PacketAddress address, string leg)
    {
        if (handle.Send(span, address, out var win32Error))
        {
            diagnostics.InjectionSucceeded();
            return;
        }

        diagnostics.InjectionFailed(win32Error, leg);
        logger.LogWarning(
            "WinDivertSend rejeitou a reinjeção na perna '{Leg}' (erro Win32 {Error}); a conexão correspondente vai travar.",
            leg, win32Error);
    }
}
