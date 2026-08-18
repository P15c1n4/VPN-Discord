using System.Collections.Concurrent;
using System.Text;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Diagnostics;

public sealed class TunnelDiagnostics
{
    private const int EVENT_LOG_CAPACITY = 300;

    private readonly ConcurrentQueue<DiagnosticEvent> _events = new();
    private readonly ProtocolCounters _tcp = new();
    private readonly ProtocolCounters _udp = new();

    private long _networkPacketsSeen;
    private long _socketEventsSeen;
    private long _pidFromSocketLayer;
    private long _pidFromIpHelper;
    private long _pidUnresolved;
    private long _notTarget;
    private long _ipv6Dropped;
    private long _injectOk;
    private long _injectFailed;
    private int _lastCaptureError;
    private int _lastInjectError;

    public long NetworkPacketsSeen => Interlocked.Read(ref _networkPacketsSeen);
    public long SocketEventsSeen => Interlocked.Read(ref _socketEventsSeen);
    public long PidFromSocketLayer => Interlocked.Read(ref _pidFromSocketLayer);
    public long PidFromIpHelper => Interlocked.Read(ref _pidFromIpHelper);
    public long PidUnresolved => Interlocked.Read(ref _pidUnresolved);
    public long NotTarget => Interlocked.Read(ref _notTarget);
    public long Ipv6Dropped => Interlocked.Read(ref _ipv6Dropped);
    public long InjectOk => Interlocked.Read(ref _injectOk);
    public long InjectFailed => Interlocked.Read(ref _injectFailed);
    public int LastCaptureError => Volatile.Read(ref _lastCaptureError);
    public int LastInjectError => Volatile.Read(ref _lastInjectError);

    public long MatchedTarget => _tcp.Matched + _udp.Matched;
    public long Redirected => _tcp.Redirected + _udp.Redirected;
    public long UpstreamConnectOk => _tcp.UpstreamOk + _udp.UpstreamOk;
    public long UpstreamConnectFailed => _tcp.UpstreamFailed + _udp.UpstreamFailed;
    public long BytesUpstream => _tcp.BytesUp + _udp.BytesUp;
    public long BytesDownstream => _tcp.BytesDown + _udp.BytesDown;

    public EgressSelfTestResult? EgressSelfTest { get; private set; }

    public TunnelProtocolScope Scope { get; private set; } = TunnelProtocolScope.TcpAndUdp;

    public ProtocolSnapshot Tcp => _tcp.Snapshot();
    public ProtocolSnapshot Udp => _udp.Snapshot();

    public event EventHandler? Updated;

    public void Reset()
    {
        Interlocked.Exchange(ref _networkPacketsSeen, 0);
        Interlocked.Exchange(ref _socketEventsSeen, 0);
        Interlocked.Exchange(ref _pidFromSocketLayer, 0);
        Interlocked.Exchange(ref _pidFromIpHelper, 0);
        Interlocked.Exchange(ref _pidUnresolved, 0);
        Interlocked.Exchange(ref _notTarget, 0);
        Interlocked.Exchange(ref _ipv6Dropped, 0);
        Interlocked.Exchange(ref _injectOk, 0);
        Interlocked.Exchange(ref _injectFailed, 0);
        Volatile.Write(ref _lastCaptureError, 0);
        Volatile.Write(ref _lastInjectError, 0);
        _tcp.Reset();
        _udp.Reset();
        EgressSelfTest = null;
        _events.Clear();
        Raise();
    }

    public void SetScope(TunnelProtocolScope scope)
    {
        Scope = scope;
        Raise();
    }

    public void PacketCaptured() => Interlocked.Increment(ref _networkPacketsSeen);

    public void SocketEventCaptured() => Interlocked.Increment(ref _socketEventsSeen);

    public void CaptureFailed(int win32Error, string stage)
    {
        Volatile.Write(ref _lastCaptureError, win32Error);
        Record(DiagnosticSeverity.Error, $"Captura interrompida · {stage} · Win32 {win32Error}");
    }

    public void OwnerResolved(bool fromSocketLayer)
    {
        if (fromSocketLayer)
        {
            Interlocked.Increment(ref _pidFromSocketLayer);
        }
        else
        {
            Interlocked.Increment(ref _pidFromIpHelper);
        }
    }

    public void OwnerUnresolved() => Interlocked.Increment(ref _pidUnresolved);

    public void NotTargetProcess() => Interlocked.Increment(ref _notTarget);

    public void Ipv6Blocked(TransportProtocol protocol, ushort localPort)
    {
        Interlocked.Increment(ref _ipv6Dropped);
        Record(DiagnosticSeverity.Info, $"IPv6 descartado · {protocol} porta {localPort}");
    }

    public void TargetMatched(TransportProtocol protocol) => For(protocol).OnMatched();

    public void PacketRedirected(TransportProtocol protocol, string destination)
    {
        For(protocol).OnRedirected();
        Record(DiagnosticSeverity.Info, $"{protocol} → {destination} · redirecionado");
    }

    public void InjectionSucceeded()
    {
        Interlocked.Increment(ref _injectOk);
    }

    public void InjectionFailed(int win32Error, string leg)
    {
        Interlocked.Increment(ref _injectFailed);
        Volatile.Write(ref _lastInjectError, win32Error);
        Record(DiagnosticSeverity.Error, $"Reinjeção falhou · {leg} · Win32 {win32Error}");
    }

    public void UpstreamConnected(TransportProtocol protocol, string destination, string localEndpoint)
    {
        For(protocol).OnUpstreamOk();
        Record(DiagnosticSeverity.Info, $"{protocol} {localEndpoint} → {destination} · estabelecido");
        Raise();
    }

    public void UpstreamFailed(TransportProtocol protocol, string destination, string reason)
    {
        For(protocol).OnUpstreamFailed();
        Record(DiagnosticSeverity.Error, $"{protocol} → {destination} · recusado · {reason}");
        Raise();
    }

    public void BytesRelayed(TransportProtocol protocol, long upstream, long downstream) =>
        For(protocol).OnBytes(upstream, downstream);

    public void SelfTestCompleted(EgressSelfTestResult result)
    {
        EgressSelfTest = result;
        Record(
            result.Success ? DiagnosticSeverity.Info : DiagnosticSeverity.Error,
            $"Autoteste · {result.Summary}");
        Raise();
    }

    public void Note(string message, DiagnosticSeverity severity = DiagnosticSeverity.Info)
    {
        Record(severity, message);
        Raise();
    }

    public IReadOnlyList<DiagnosticEvent> RecentEvents() => [.. _events];

    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Diagnóstico do túnel");
        sb.AppendLine($"Escopo             {DescribeScope(Scope)}");
        sb.AppendLine($"Captura            {NetworkPacketsSeen} pacotes · {SocketEventsSeen} eventos de socket" +
                      (LastCaptureError == 0 ? "" : $" · Win32 {LastCaptureError}"));
        sb.AppendLine($"Processo           {PidFromSocketLayer} socket · {PidFromIpHelper} IP Helper · {PidUnresolved} sem dono");
        sb.AppendLine($"Alvo               {MatchedTarget} de {MatchedTarget + NotTarget}");
        sb.AppendLine($"Redirecionamento   TCP {Tcp.Redirected} · UDP {Udp.Redirected} · IPv6 descartado {Ipv6Dropped}");
        sb.AppendLine($"Saída VPN          TCP {Tcp.UpstreamOk}/{Tcp.UpstreamFailed} · UDP {Udp.UpstreamOk}/{Udp.UpstreamFailed} (ok/falhas)");
        sb.AppendLine($"Retorno            TCP {FormatBytes(Tcp.BytesUp)}↑ {FormatBytes(Tcp.BytesDown)}↓ · " +
                      $"UDP {FormatBytes(Udp.BytesUp)}↑ {FormatBytes(Udp.BytesDown)}↓");
        sb.AppendLine($"Reinjeção          {InjectOk} ok · {InjectFailed} falhas" +
                      (LastInjectError == 0 ? "" : $" · Win32 {LastInjectError}"));
        sb.AppendLine($"Autoteste          {EgressSelfTest?.Summary ?? "não executado"}");
        sb.AppendLine();
        sb.AppendLine("Eventos");
        foreach (var evt in _events.Reverse().Take(40))
        {
            sb.AppendLine($"  {evt.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  {evt.Severity,-7}  {evt.Message}");
        }

        return sb.ToString();
    }

    public static string DescribeScope(TunnelProtocolScope scope) => scope switch
    {
        TunnelProtocolScope.TcpOnly => "TCP",
        TunnelProtocolScope.UdpOnly => "UDP",
        _ => "TCP e UDP",
    };

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };

    private ProtocolCounters For(TransportProtocol protocol) =>
        protocol == TransportProtocol.Tcp ? _tcp : _udp;

    private void Record(DiagnosticSeverity severity, string message)
    {
        _events.Enqueue(new DiagnosticEvent(DateTime.UtcNow, severity, message));
        while (_events.Count > EVENT_LOG_CAPACITY && _events.TryDequeue(out _))
        {
        }
    }

    private void Raise() => Updated?.Invoke(this, EventArgs.Empty);

    private sealed class ProtocolCounters
    {
        private long _matched;
        private long _redirected;
        private long _upstreamOk;
        private long _upstreamFailed;
        private long _bytesUp;
        private long _bytesDown;

        public long Matched => Interlocked.Read(ref _matched);
        public long Redirected => Interlocked.Read(ref _redirected);
        public long UpstreamOk => Interlocked.Read(ref _upstreamOk);
        public long UpstreamFailed => Interlocked.Read(ref _upstreamFailed);
        public long BytesUp => Interlocked.Read(ref _bytesUp);
        public long BytesDown => Interlocked.Read(ref _bytesDown);

        public void OnMatched() => Interlocked.Increment(ref _matched);
        public void OnRedirected() => Interlocked.Increment(ref _redirected);
        public void OnUpstreamOk() => Interlocked.Increment(ref _upstreamOk);
        public void OnUpstreamFailed() => Interlocked.Increment(ref _upstreamFailed);

        public void OnBytes(long up, long down)
        {
            if (up != 0)
            {
                Interlocked.Add(ref _bytesUp, up);
            }

            if (down != 0)
            {
                Interlocked.Add(ref _bytesDown, down);
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _matched, 0);
            Interlocked.Exchange(ref _redirected, 0);
            Interlocked.Exchange(ref _upstreamOk, 0);
            Interlocked.Exchange(ref _upstreamFailed, 0);
            Interlocked.Exchange(ref _bytesUp, 0);
            Interlocked.Exchange(ref _bytesDown, 0);
        }

        public ProtocolSnapshot Snapshot() =>
            new(Matched, Redirected, UpstreamOk, UpstreamFailed, BytesUp, BytesDown);
    }
}

public readonly record struct ProtocolSnapshot(
    long Matched,
    long Redirected,
    long UpstreamOk,
    long UpstreamFailed,
    long BytesUp,
    long BytesDown);

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record DiagnosticEvent(DateTime TimestampUtc, DiagnosticSeverity Severity, string Message);

public sealed record EgressSelfTestResult(
    bool Success,
    string Summary,
    string? PublicIpThroughVpn = null,
    string? PublicIpDirect = null,
    bool UdpWorks = false);
