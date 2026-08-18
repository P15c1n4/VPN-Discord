using CommunityToolkit.Mvvm.ComponentModel;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Presentation.Wpf.ViewModels;

public sealed partial class VpnGateServerRowViewModel(VpnGateServerEntry entry) : ObservableObject
{
    public VpnGateServerEntry Entry { get; } = entry;

    public string HostName => Entry.HostName;
    public string CountryLong => Entry.CountryLong;

    public string Address => Entry.IpAddress;

    public int Port => Entry.EndpointFor(Entry.PreferredProtocol)?.Port ?? 0;

    public string Protocols => Entry.ProtocolSummary;

    public int Score => Entry.Score;

    [ObservableProperty]
    private string _latencyText = "aguardando";

    [ObservableProperty]
    private int? _latencyMs;

    [ObservableProperty]
    private bool _pingFailed;

    public void ReportResult(PingResult result)
    {
        if (result.Success && result.Latency is { } latency)
        {
            LatencyMs = (int)latency.TotalMilliseconds;
            LatencyText = $"{LatencyMs} ms";
            PingFailed = false;
        }
        else
        {
            LatencyMs = null;
            LatencyText = "falha";
            PingFailed = true;
        }
    }
}
