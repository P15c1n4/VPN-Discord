using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyDiscord.Application.Diagnostics;

namespace ProxyDiscord.Presentation.Wpf.ViewModels;

public sealed record DiagnosticStage(string Step, string Value, bool IsHealthy);

public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan REFRESH_INTERVAL = TimeSpan.FromSeconds(1);
    private static readonly string LOG_DIRECTORY = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProxyDiscord", "logs");

    private readonly TunnelDiagnostics _diagnostics;
    private readonly DispatcherTimer _timer;
    private long _lastTcpBytesUp;
    private long _lastTcpBytesDown;
    private long _lastUdpBytesUp;
    private long _lastUdpBytesDown;
    private long _lastSampleTimestamp;

    public DiagnosticsViewModel(TunnelDiagnostics diagnostics, Dispatcher dispatcher)
    {
        _diagnostics = diagnostics;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = REFRESH_INTERVAL };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    public ObservableCollection<DiagnosticStage> Stages { get; } = [];

    public ObservableCollection<string> Events { get; } = [];

    [ObservableProperty]
    private string _scopeText = "";

    public void Refresh()
    {
        var tcp = _diagnostics.Tcp;
        var udp = _diagnostics.Udp;
        var selfTest = _diagnostics.EgressSelfTest;
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = _lastSampleTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds;
        var tcpUpRate = CalculateRate(tcp.BytesUp, _lastTcpBytesUp, elapsedSeconds);
        var tcpDownRate = CalculateRate(tcp.BytesDown, _lastTcpBytesDown, elapsedSeconds);
        var udpUpRate = CalculateRate(udp.BytesUp, _lastUdpBytesUp, elapsedSeconds);
        var udpDownRate = CalculateRate(udp.BytesDown, _lastUdpBytesDown, elapsedSeconds);
        _lastTcpBytesUp = tcp.BytesUp;
        _lastTcpBytesDown = tcp.BytesDown;
        _lastUdpBytesUp = udp.BytesUp;
        _lastUdpBytesDown = udp.BytesDown;
        _lastSampleTimestamp = now;

        var stages = new List<DiagnosticStage>
        {
            new("Captura de tráfego",
                $"{_diagnostics.NetworkPacketsSeen} pacotes; {_diagnostics.SocketEventsSeen} eventos de socket" +
                (_diagnostics.LastCaptureError == 0 ? "" : $"; código Win32 {_diagnostics.LastCaptureError}"),
                _diagnostics.NetworkPacketsSeen > 0 && _diagnostics.LastCaptureError == 0),

            new("Identificação de PID",
                $"{_diagnostics.PidFromSocketLayer} por socket; {_diagnostics.PidFromIpHelper} via IP Helper; " +
                $"{_diagnostics.PidUnresolved} sem PID",
                _diagnostics.PidFromSocketLayer + _diagnostics.PidFromIpHelper > 0),

            new("Processo monitorado",
                $"{_diagnostics.MatchedTarget} pacotes de {_diagnostics.MatchedTarget + _diagnostics.NotTarget} avaliados",
                _diagnostics.MatchedTarget > 0),

            new("Redirecionamento",
                $"TCP {tcp.Redirected}; UDP {udp.Redirected}" +
                (_diagnostics.Ipv6Dropped > 0 ? $"; IPv6 ignorado {_diagnostics.Ipv6Dropped}" : ""),
                tcp.Redirected + udp.Redirected > 0),

            new("Conexões pela VPN",
                $"TCP {tcp.UpstreamOk} ok/{tcp.UpstreamFailed} falhas; UDP {udp.UpstreamOk} ok/{udp.UpstreamFailed} falhas",
                tcp.UpstreamOk + udp.UpstreamOk > 0 && tcp.UpstreamFailed + udp.UpstreamFailed == 0),

            new("Dados transferidos",
                $"TCP: {Format(tcp.BytesUp)} enviados, {Format(tcp.BytesDown)} recebidos\n" +
                $"TCP: {FormatRate(tcpUpRate)} enviados, {FormatRate(tcpDownRate)} recebidos",
                tcp.BytesUp + tcp.BytesDown > 0),

            new("Dados transferidos",
                $"UDP: {Format(udp.BytesUp)} enviados, {Format(udp.BytesDown)} recebidos\n" +
                $"UDP: {FormatRate(udpUpRate)} enviados, {FormatRate(udpDownRate)} recebidos",
                udp.BytesUp + udp.BytesDown > 0),

            new("Pacotes reinjetados",
                $"{_diagnostics.InjectOk} ok; {_diagnostics.InjectFailed} falhas" +
                (_diagnostics.LastInjectError == 0 ? "" : $"; código Win32 {_diagnostics.LastInjectError}"),
                _diagnostics.InjectOk > 0 && _diagnostics.InjectFailed == 0),

            new("Teste de saída",
                selfTest?.Summary ?? "Não executado",
                selfTest?.Success ?? false),
        };

        Stages.Clear();
        foreach (var stage in stages)
        {
            Stages.Add(stage);
        }

        Events.Clear();
        foreach (var evt in _diagnostics.RecentEvents().Reverse().Take(80))
        {
            Events.Add($"{evt.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  {TunnelDiagnostics.DescribeSeverity(evt.Severity),-13}  {evt.Message}");
        }

        ScopeText = TunnelDiagnostics.DescribeScope(_diagnostics.Scope);
    }

    private static string Format(long bytes) => TunnelDiagnostics.FormatBytes(bytes);

    private static long CalculateRate(long current, long previous, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0 || current <= previous)
        {
            return 0;
        }

        return (long)((current - previous) / elapsedSeconds);
    }

    private static string FormatRate(long bytesPerSecond) => $"{Format(bytesPerSecond)}/s";

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(LOG_DIRECTORY);
        Process.Start(new ProcessStartInfo { FileName = LOG_DIRECTORY, UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyReport()
    {
        try
        {
            System.Windows.Clipboard.SetText(_diagnostics.BuildReport());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    public void Dispose() => _timer.Stop();
}
