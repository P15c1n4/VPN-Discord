using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Presentation.Wpf.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const string DEFAULT_PROCESS_NAME = "Discord";

    private readonly ConnectVpnUseCase _connectVpnUseCase;
    private readonly DisconnectVpnUseCase _disconnectVpnUseCase;
    private readonly DiscoverRunningProcessesUseCase _discoverProcessesUseCase;
    private readonly RoutingSessionContext _sessionContext;
    private readonly LoadOpenVpnProfileUseCase _loadOpenVpnProfileUseCase;
    private readonly Func<ProcessPickerWindowResult?> _openProcessPicker;
    private readonly BrowseForExecutable _browseForExecutable;
    private readonly BrowseForOpenVpnProfile _browseForOpenVpnProfile;
    private readonly Action _showDiagnostics;
    private readonly ILogger<MainWindowViewModel> _logger;

    private string? _selectedOpenVpnConfig;

    public MainWindowViewModel(
        ConnectVpnUseCase connectVpnUseCase,
        DisconnectVpnUseCase disconnectVpnUseCase,
        DiscoverRunningProcessesUseCase discoverProcessesUseCase,
        LoadOpenVpnProfileUseCase loadOpenVpnProfileUseCase,
        RoutingSessionContext sessionContext,
        VpnGateListViewModel vpnGateList,
        Func<ProcessPickerWindowResult?> openProcessPicker,
        BrowseForExecutable browseForExecutable,
        BrowseForOpenVpnProfile browseForOpenVpnProfile,
        Action showDiagnostics,
        ILogger<MainWindowViewModel> logger)
    {
        _connectVpnUseCase = connectVpnUseCase;
        _disconnectVpnUseCase = disconnectVpnUseCase;
        _discoverProcessesUseCase = discoverProcessesUseCase;
        _loadOpenVpnProfileUseCase = loadOpenVpnProfileUseCase;
        _sessionContext = sessionContext;
        _openProcessPicker = openProcessPicker;
        _browseForExecutable = browseForExecutable;
        _browseForOpenVpnProfile = browseForOpenVpnProfile;
        _showDiagnostics = showDiagnostics;
        _logger = logger;

        VpnGateList = vpnGateList;
        VpnGateList.ServerSelected += OnVpnGateServerSelected;

        _sessionContext.PropertyChanged += (_, _) => RefreshFromSession();
        RefreshFromSession();
    }

    public VpnGateListViewModel VpnGateList { get; }

    [ObservableProperty]
    private IReadOnlyList<VpnProtocol> _protocols = [VpnProtocol.OpenVpn, VpnProtocol.Sstp];

    private VpnGateServerEntry? _selectedServer;

    [ObservableProperty]
    private ProcessInfo? _selectedProcess;

    [ObservableProperty]
    private string _selectedProcessDisplay = $"{DEFAULT_PROCESS_NAME}.exe (verificando...)";

    [ObservableProperty]
    private string _serverHost = "";

    [ObservableProperty]
    private string _serverPort = "443";

    [ObservableProperty]
    private VpnProtocol _selectedProtocol = VpnProtocol.OpenVpn;

    public IReadOnlyList<TunnelProtocolScope> ProtocolScopes { get; } =
        [TunnelProtocolScope.TcpOnly, TunnelProtocolScope.UdpOnly, TunnelProtocolScope.TcpAndUdp];

    [ObservableProperty]
    private TunnelProtocolScope _selectedProtocolScope = TunnelProtocolScope.TcpOnly;

    [ObservableProperty]
    private string _openVpnProfileSource = "";

    [ObservableProperty]
    private string _username = "vpn";

    [ObservableProperty]
    private string _password = "vpn";

    [ObservableProperty]
    private string _dnsServer = TunnelDnsSettings.GOOGLE_PUBLIC_DNS;

    public IReadOnlyList<string> DnsSuggestions { get; } = TunnelDnsSettings.Suggestions;

    [ObservableProperty]
    private ConnectionStatus _status = ConnectionStatus.Idle;

    [ObservableProperty]
    private string _statusText = "Inativo";

    [ObservableProperty]
    private string? _latencyText;

    [ObservableProperty]
    private string? _errorMessage;

    public bool CanConnect =>
        Status is ConnectionStatus.Idle or ConnectionStatus.Error
        && SelectedProcess is not null
        && !string.IsNullOrWhiteSpace(ServerHost)
        && !string.IsNullOrWhiteSpace(Username);

    public bool CanDisconnect => Status is ConnectionStatus.Connecting or ConnectionStatus.Connected;

    partial void OnSelectedProcessChanged(ProcessInfo? value) => ConnectCommand.NotifyCanExecuteChanged();

    partial void OnUsernameChanged(string value) => ConnectCommand.NotifyCanExecuteChanged();

    public async Task InitializeAsync()
    {
        try
        {
            var processes = await _discoverProcessesUseCase.ExecuteAsync();
            var discord = processes.FirstOrDefault(p => string.Equals(p.Name, DEFAULT_PROCESS_NAME, StringComparison.OrdinalIgnoreCase));
            SelectedProcess = discord;
            SelectedProcessDisplay = discord is not null
                ? $"{discord.Name}.exe (PID {discord.Pid})"
                : $"{DEFAULT_PROCESS_NAME}.exe (não está em execução)";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao localizar o processo padrão do Discord");
            SelectedProcessDisplay = $"{DEFAULT_PROCESS_NAME}.exe (falha ao detectar)";
        }
    }

    [RelayCommand]
    private async Task OpenProcessPickerAsync()
    {
        var result = _openProcessPicker();
        if (result is null)
        {
            return;
        }

        await ApplyTargetAsync(result.Process, $"{result.Process.Name} (PID {result.Process.Pid})");
    }

    [RelayCommand]
    private async Task BrowseForExecutableAsync()
    {
        var path = _browseForExecutable();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        await ApplyTargetAsync(new ProcessInfo(0, name, path), $"{Path.GetFileName(path)} (aguardando iniciar)");
    }

    [RelayCommand]
    private async Task LoadOpenVpnProfileAsync()
    {
        var path = _browseForOpenVpnProfile();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var result = await _loadOpenVpnProfileUseCase.ExecuteAsync(path);
        if (result.Profile is not { } profile)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        _selectedServer = null;
        _selectedOpenVpnConfig = profile.ConfigBase64;
        OpenVpnProfileSource = $"{profile.FileName} · {profile.Endpoint.Host}:{profile.Endpoint.Port} " +
                               $"({profile.Transport.ToString().ToUpperInvariant()})";

        Protocols = [VpnProtocol.OpenVpn];
        SelectedProtocol = VpnProtocol.OpenVpn;
        ServerHost = profile.Endpoint.Host;
        ServerPort = profile.Endpoint.Port.ToString();
        ErrorMessage = null;
    }

    private async Task ApplyTargetAsync(ProcessInfo process, string display)
    {
        if (CanDisconnect)
        {
            await _disconnectVpnUseCase.ExecuteAsync();
        }

        SelectedProcess = process;
        SelectedProcessDisplay = display;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedProcess is null)
        {
            return;
        }

        ErrorMessage = null;

        if (SelectedProtocol == VpnProtocol.OpenVpn && string.IsNullOrWhiteSpace(_selectedOpenVpnConfig))
        {
            ErrorMessage =
                "OpenVPN exige um perfil: escolha um servidor na lista ou carregue um arquivo .ovpn.";
            return;
        }

        var command = new ConnectVpnCommand(
            SelectedProcess,
            ComposeServerAddress(),
            SelectedProtocol,
            Username,
            Password,
            _selectedOpenVpnConfig,
            new TunnelDnsSettings(DnsServer),
            SelectedProtocolScope);

        var result = await _connectVpnUseCase.ExecuteAsync(command);
        if (!result.Success)
        {
            ErrorMessage = result.ErrorMessage;
        }
    }

    private string ComposeServerAddress() =>
        string.IsNullOrWhiteSpace(ServerPort) ? ServerHost.Trim() : $"{ServerHost.Trim()}:{ServerPort.Trim()}";

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await _disconnectVpnUseCase.ExecuteAsync();
    }

    private void OnVpnGateServerSelected(VpnGateServerEntry entry)
    {
        _selectedServer = entry;
        _selectedOpenVpnConfig = entry.SupportsOpenVpn ? entry.OpenVpnConfigBase64 : null;
        OpenVpnProfileSource = entry.SupportsOpenVpn ? $"VPN Gate · {entry.HostName}" : "";

        Protocols = entry.SupportedProtocols;
        SelectedProtocol = entry.PreferredProtocol;
        ApplyEndpointForProtocol(entry, entry.PreferredProtocol);
    }

    partial void OnSelectedProtocolChanged(VpnProtocol value)
    {
        if (_selectedServer is { } entry)
        {
            ApplyEndpointForProtocol(entry, value);
        }
    }

    private void ApplyEndpointForProtocol(VpnGateServerEntry entry, VpnProtocol protocol)
    {
        if (entry.EndpointFor(protocol) is not { } endpoint)
        {
            return;
        }

        ServerHost = endpoint.Host;
        ServerPort = endpoint.Port.ToString();
    }

    [RelayCommand]
    private void OpenDiagnostics() => _showDiagnostics();

    partial void OnServerHostChanged(string value)
    {
        ConnectCommand.NotifyCanExecuteChanged();

        var lastColon = value.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= value.Length - 1)
        {
            return;
        }

        var portPart = value[(lastColon + 1)..];
        if (int.TryParse(portPart, out var port) && port is > 0 and <= 65535)
        {
            ServerHost = value[..lastColon];
            ServerPort = port.ToString();
        }
    }

    private void RefreshFromSession()
    {
        Status = _sessionContext.Status;
        StatusText = Status switch
        {
            ConnectionStatus.Idle => "Inativo",
            ConnectionStatus.Connecting => "Conectando...",
            ConnectionStatus.Connected => "Conectado",
            ConnectionStatus.Error => "Erro",
            _ => "Inativo"
        };
        LatencyText = _sessionContext.Latency is { } latency ? $"{latency.TotalMilliseconds:F0} ms" : null;
        ErrorMessage = _sessionContext.LastError;

        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => VpnGateList.ServerSelected -= OnVpnGateServerSelected;
}

public sealed record ProcessPickerWindowResult(ProcessInfo Process);

public delegate string? BrowseForExecutable();

public delegate string? BrowseForOpenVpnProfile();
