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

        var stages = new List<DiagnosticStage>
        {
            new("Captura",
                $"{_diagnostics.NetworkPacketsSeen} pacotes · {_diagnostics.SocketEventsSeen} eventos de socket" +
                (_diagnostics.LastCaptureError == 0 ? "" : $" · Win32 {_diagnostics.LastCaptureError}"),
                _diagnostics.NetworkPacketsSeen > 0 && _diagnostics.LastCaptureError == 0),

            new("Processo",
                $"{_diagnostics.PidFromSocketLayer} socket · {_diagnostics.PidFromIpHelper} IP Helper · " +
                $"{_diagnostics.PidUnresolved} sem dono",
                _diagnostics.PidFromSocketLayer + _diagnostics.PidFromIpHelper > 0),

            new("Alvo",
                $"{_diagnostics.MatchedTarget} de {_diagnostics.MatchedTarget + _diagnostics.NotTarget}",
                _diagnostics.MatchedTarget > 0),

            new("Redirecionamento",
                $"TCP {tcp.Redirected} · UDP {udp.Redirected}" +
                (_diagnostics.Ipv6Dropped > 0 ? $" · IPv6 descartado {_diagnostics.Ipv6Dropped}" : ""),
                tcp.Redirected + udp.Redirected > 0),

            new("Saída VPN",
                $"TCP {tcp.UpstreamOk}/{tcp.UpstreamFailed} · UDP {udp.UpstreamOk}/{udp.UpstreamFailed} (ok/falhas)",
                tcp.UpstreamOk + udp.UpstreamOk > 0 && tcp.UpstreamFailed + udp.UpstreamFailed == 0),

            new("Retorno",
                $"TCP {Format(tcp.BytesUp)}↑ {Format(tcp.BytesDown)}↓ · UDP {Format(udp.BytesUp)}↑ {Format(udp.BytesDown)}↓",
                tcp.BytesDown + udp.BytesDown > 0),

            new("Reinjeção",
                $"{_diagnostics.InjectOk} ok · {_diagnostics.InjectFailed} falhas" +
                (_diagnostics.LastInjectError == 0 ? "" : $" · Win32 {_diagnostics.LastInjectError}"),
                _diagnostics.InjectOk > 0 && _diagnostics.InjectFailed == 0),

            new("Autoteste",
                selfTest?.Summary ?? "não executado",
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
            Events.Add($"{evt.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  {evt.Severity,-7}  {evt.Message}");
        }

        ScopeText = TunnelDiagnostics.DescribeScope(_diagnostics.Scope);
    }

    private static string Format(long bytes) => TunnelDiagnostics.FormatBytes(bytes);

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
