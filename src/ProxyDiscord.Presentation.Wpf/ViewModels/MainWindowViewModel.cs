using System.IO;
using System.Windows.Threading;
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
    private readonly SavedCredentialsUseCase _savedCredentialsUseCase;
    private readonly Func<ProcessPickerWindowResult?> _openProcessPicker;
    private readonly BrowseForExecutable _browseForExecutable;
    private readonly BrowseForOpenVpnProfile _browseForOpenVpnProfile;
    private readonly ChooseOpenVpnCredentialSource _chooseOpenVpnCredentialSource;
    private readonly CheckForUpdatesUseCase _checkForUpdatesUseCase;
    private readonly LaunchUpdateUseCase _launchUpdateUseCase;
    private readonly ConfirmUpdatePrompt _confirmUpdatePrompt;
    private readonly UpdateCheckMessage _updateCheckMessage;
    private readonly ExitForUpdate _exitForUpdate;
    private readonly Action _showDiagnostics;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    private string? _selectedOpenVpnConfig;
    private bool _applyingCredentials;
    private bool _applyingVpnGateSelection;
    private bool _usernameWasManuallyEntered;
    private bool _passwordWasManuallyEntered;
    private string? _manualPasswordServerKey;
    private string? _manualPasswordValue;
    private string? _lastCredentialServerKey;
    private bool _credentialsWereLoadedFromStore;
    private OpenVpnAuthenticationInfo? _localProfileAuthentication;
    private bool _isLocalOpenVpnProfile;
    [ObservableProperty]
    private bool _isCheckingForUpdates;

    public MainWindowViewModel(
        ConnectVpnUseCase connectVpnUseCase,
        DisconnectVpnUseCase disconnectVpnUseCase,
        DiscoverRunningProcessesUseCase discoverProcessesUseCase,
        LoadOpenVpnProfileUseCase loadOpenVpnProfileUseCase,
        SavedCredentialsUseCase savedCredentialsUseCase,
        RoutingSessionContext sessionContext,
        VpnGateListViewModel vpnGateList,
        Func<ProcessPickerWindowResult?> openProcessPicker,
        BrowseForExecutable browseForExecutable,
        BrowseForOpenVpnProfile browseForOpenVpnProfile,
        ChooseOpenVpnCredentialSource chooseOpenVpnCredentialSource,
        CheckForUpdatesUseCase checkForUpdatesUseCase,
        LaunchUpdateUseCase launchUpdateUseCase,
        ConfirmUpdatePrompt confirmUpdatePrompt,
        UpdateCheckMessage updateCheckMessage,
        ExitForUpdate exitForUpdate,
        Action showDiagnostics,
        Dispatcher dispatcher,
        ILogger<MainWindowViewModel> logger)
    {
        _connectVpnUseCase = connectVpnUseCase;
        _disconnectVpnUseCase = disconnectVpnUseCase;
        _discoverProcessesUseCase = discoverProcessesUseCase;
        _loadOpenVpnProfileUseCase = loadOpenVpnProfileUseCase;
        _savedCredentialsUseCase = savedCredentialsUseCase;
        _sessionContext = sessionContext;
        _openProcessPicker = openProcessPicker;
        _browseForExecutable = browseForExecutable;
        _browseForOpenVpnProfile = browseForOpenVpnProfile;
        _chooseOpenVpnCredentialSource = chooseOpenVpnCredentialSource;
        _checkForUpdatesUseCase = checkForUpdatesUseCase;
        _launchUpdateUseCase = launchUpdateUseCase;
        _confirmUpdatePrompt = confirmUpdatePrompt;
        _updateCheckMessage = updateCheckMessage;
        _exitForUpdate = exitForUpdate;
        _showDiagnostics = showDiagnostics;
        _dispatcher = dispatcher;
        _logger = logger;

        VpnGateList = vpnGateList;
        VpnGateList.ServerSelected += OnVpnGateServerSelected;

        _sessionContext.PropertyChanged += OnSessionPropertyChanged;
        RefreshFromSession();
    }

    public VpnGateListViewModel VpnGateList { get; }

    [ObservableProperty]
    private IReadOnlyList<VpnProtocol> _protocols = [VpnProtocol.OpenVpn, VpnProtocol.Sstp];

    private VpnGateServerEntry? _selectedServer;

    [ObservableProperty]
    private ProcessInfo? _selectedProcess;

    [ObservableProperty]
    private string _selectedProcessDisplay = $"{DEFAULT_PROCESS_NAME}.exe (verificando)";

    [ObservableProperty]
    private string _serverHost = "";

    [ObservableProperty]
    private string _serverPort = "443";

    [ObservableProperty]
    private VpnProtocol _selectedProtocol = VpnProtocol.OpenVpn;

    public IReadOnlyList<TunnelProtocolScope> ProtocolScopes { get; } =
        [TunnelProtocolScope.TcpAndUdp, TunnelProtocolScope.TcpOnly, TunnelProtocolScope.UdpOnly];

    [ObservableProperty]
    private TunnelProtocolScope _selectedProtocolScope = TunnelProtocolScope.TcpAndUdp;

    [ObservableProperty]
    private string _openVpnProfileSource = "";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private bool _saveCredentialsEnabled;

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

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasErrorMessage));

    public bool CanConnect =>
        Status is ConnectionStatus.Idle or ConnectionStatus.Error
        && SelectedProcess is not null
        && !string.IsNullOrWhiteSpace(ServerHost)
        && (SelectedProtocol != VpnProtocol.Sstp ||
            (!string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password)));

    public bool CanDisconnect => Status is ConnectionStatus.Connecting or ConnectionStatus.Connected;

    public Task CheckForUpdatesOnStartupAsync() => CheckForUpdatesCoreAsync(showNoUpdateMessage: false);

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdates() => CheckForUpdatesCoreAsync(showNoUpdateMessage: true);

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    partial void OnIsCheckingForUpdatesChanged(bool value) => CheckForUpdatesCommand.NotifyCanExecuteChanged();

    private async Task CheckForUpdatesCoreAsync(bool showNoUpdateMessage)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        var currentVersion = GetCurrentApplicationVersion();
        try
        {
            var check = await _checkForUpdatesUseCase.ExecuteAsync(currentVersion);
            if (!check.IsUpdateAvailable)
            {
                if (showNoUpdateMessage)
                {
                    _updateCheckMessage(check.LatestRelease is null
                        ? "Nenhuma versão foi encontrada nas releases do GitHub."
                        : $"Você já está usando a versão mais recente ({currentVersion}).");
                }

                return;
            }

            var release = check.LatestRelease!;
            if (release.Package is null)
            {
                _logger.LogWarning(
                    "A release {Tag} não publicou o asset {AssetName} esperado.",
                    release.TagName, "Discord-VPN-win-x64.zip");
                if (showNoUpdateMessage)
                {
                    _updateCheckMessage(
                        $"A versão {release.TagName} está disponível, mas o pacote de atualização ainda não foi publicado.\n" +
                        release.ReleasePageUri);
                }

                return;
            }

            if (!_confirmUpdatePrompt(currentVersion, release))
            {
                return;
            }

            await _launchUpdateUseCase.ExecuteAsync(
                currentVersion,
                release,
                Environment.ProcessId,
                AppContext.BaseDirectory);
            _exitForUpdate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível verificar ou iniciar uma atualização do aplicativo");
            if (showNoUpdateMessage)
            {
                _updateCheckMessage($"Não foi possível verificar ou iniciar a atualização.\n\n{ex.Message}");
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private static Version GetCurrentApplicationVersion()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    partial void OnSelectedProcessChanged(ProcessInfo? value) => ConnectCommand.NotifyCanExecuteChanged();

    partial void OnUsernameChanged(string value)
    {
        if (!_applyingCredentials)
        {
            _usernameWasManuallyEntered = true;
        }

        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnPasswordChanged(string value) => ConnectCommand.NotifyCanExecuteChanged();

    public async Task InitializeAsync()
    {
        try
        {
            SaveCredentialsEnabled = (await _savedCredentialsUseCase.ReadConfigurationAsync()).SaveCredentialsEnabled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao carregar preferência de credenciais salvas");
        }

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
            SelectedProcessDisplay = $"{DEFAULT_PROCESS_NAME}.exe (não foi possível localizar)";
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
        await ApplyTargetAsync(new ProcessInfo(0, name, path), $"{Path.GetFileName(path)} (aguardando o processo iniciar)");
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

        _applyingVpnGateSelection = true;
        try
        {
            _selectedServer = null;
            _selectedOpenVpnConfig = profile.ConfigBase64;
            _localProfileAuthentication = profile.Authentication;
            _isLocalOpenVpnProfile = true;
            ResetCredentialTarget();
            SetCredentialsWithoutMarkingManual("", "");
            OpenVpnProfileSource = $"{profile.FileName} · {profile.Endpoint.Host}:{profile.Endpoint.Port} " +
                                   $"({profile.Transport.ToString().ToUpperInvariant()})";

            Protocols = [VpnProtocol.OpenVpn];
            SelectedProtocol = VpnProtocol.OpenVpn;
            ServerHost = profile.Endpoint.Host;
            ServerPort = profile.Endpoint.Port.ToString();
            ErrorMessage = null;
        }
        finally
        {
            _applyingVpnGateSelection = false;
        }

        await LoadSavedCredentialsForCurrentServerAsync();
    }

    public async Task SetSaveCredentialsEnabledAsync(bool enabled)
    {
        var previousValue = SaveCredentialsEnabled;
        SaveCredentialsEnabled = enabled;

        try
        {
            await _savedCredentialsUseCase.WriteConfigurationAsync(new UserConfiguration(enabled));
        }
        catch (Exception ex)
        {
            SaveCredentialsEnabled = previousValue;
            ErrorMessage = "Não foi possível salvar as configurações. Tente novamente.";
            _logger.LogWarning(ex, "Falha ao salvar preferência de credenciais");
            return;
        }

        if (enabled)
        {
            await LoadSavedCredentialsForCurrentServerAsync();
        }
        else if (_credentialsWereLoadedFromStore)
        {
            _applyingCredentials = true;
            try
            {
                if (!_usernameWasManuallyEntered)
                {
                    Username = "";
                }

                if (!_passwordWasManuallyEntered)
                {
                    Password = "";
                    _manualPasswordServerKey = null;
                    _manualPasswordValue = null;
                }
            }
            finally
            {
                _applyingCredentials = false;
            }

            _credentialsWereLoadedFromStore = false;
        }
    }

    public void SetPasswordFromUser(string value)
    {
        if (_applyingCredentials)
        {
            return;
        }

        _passwordWasManuallyEntered = true;
        _manualPasswordServerKey = CreateCredentialServerKey();
        _manualPasswordValue = string.IsNullOrEmpty(value) ? null : value;
        Password = value;
    }

    public async Task LoadSavedCredentialsForCurrentServerAsync()
    {
        var serverKey = CreateCredentialServerKey();
        if (serverKey is null)
        {
            return;
        }

        if (!string.Equals(_lastCredentialServerKey, serverKey, StringComparison.Ordinal))
        {
            if (_lastCredentialServerKey is not null)
            {
                ResetCredentialTarget();
                SetCredentialsWithoutMarkingManual("", "");
            }

            _lastCredentialServerKey = serverKey;
        }

        if (!SaveCredentialsEnabled)
        {
            return;
        }

        try
        {
            var credentials = await _savedCredentialsUseCase.FindAsync(serverKey);
            if (!SaveCredentialsEnabled || credentials is null ||
                !string.Equals(CreateCredentialServerKey(), serverKey, StringComparison.Ordinal))
            {
                return;
            }

            _applyingCredentials = true;
            try
            {
                if (!_usernameWasManuallyEntered)
                {
                    Username = credentials.Username;
                    _usernameWasManuallyEntered = false;
                }

                if (!_passwordWasManuallyEntered ||
                    !string.Equals(_manualPasswordServerKey, serverKey, StringComparison.Ordinal))
                {
                    if (credentials.Password is not null)
                    {
                        Password = credentials.Password;
                    }

                    _passwordWasManuallyEntered = false;
                    _manualPasswordServerKey = null;
                    _manualPasswordValue = null;
                }

                _credentialsWereLoadedFromStore = true;
            }
            finally
            {
                _applyingCredentials = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar credenciais salvas para o servidor {ServerKey}", serverKey);
        }
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
                "Selecione um servidor VPN Gate ou carregue um perfil .ovpn para usar o OpenVPN.";
            return;
        }

        if (!PromptForOpenVpnCredentialSource(out var useProfileCredentials))
        {
            return;
        }

        var command = new ConnectVpnCommand(
            SelectedProcess,
            ComposeServerAddress(),
            SelectedProtocol,
            useProfileCredentials ? null : Username,
            useProfileCredentials ? null : Password,
            _selectedOpenVpnConfig,
            new TunnelDnsSettings(DnsServer),
            SelectedProtocolScope,
            useProfileCredentials);

        var result = await _connectVpnUseCase.ExecuteAsync(command);
        if (!result.Success)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        await SaveCredentialsAfterSuccessfulConnectionAsync(credentialsWereUsed: !useProfileCredentials);
    }

    private string ComposeServerAddress() =>
        string.IsNullOrWhiteSpace(ServerPort) ? ServerHost.Trim() : $"{ServerHost.Trim()}:{ServerPort.Trim()}";

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await _disconnectVpnUseCase.ExecuteAsync();
    }

    [RelayCommand]
    private async Task CleanupResourcesAsync()
    {
        await _disconnectVpnUseCase.ExecuteAsync();
    }

    private async void OnVpnGateServerSelected(VpnGateServerEntry entry)
    {
        _selectedServer = entry;
        _localProfileAuthentication = null;
        _isLocalOpenVpnProfile = false;
        _applyingVpnGateSelection = true;
        try
        {
            _selectedOpenVpnConfig = entry.SupportsOpenVpn ? entry.OpenVpnConfigBase64 : null;
            OpenVpnProfileSource = entry.SupportsOpenVpn ? $"VPN Gate · {entry.HostName}" : "";
            Protocols = entry.SupportedProtocols;
            SelectedProtocol = entry.PreferredProtocol;
            ApplyEndpointForProtocol(entry, entry.PreferredProtocol);

            ResetCredentialTarget();
            SetCredentialsWithoutMarkingManual(
                entry.SupportsOpenVpn && entry.PreferredProtocol == VpnProtocol.OpenVpn ? "vpn" : "",
                entry.SupportsOpenVpn && entry.PreferredProtocol == VpnProtocol.OpenVpn ? "vpn" : "");
        }
        finally
        {
            _applyingVpnGateSelection = false;
        }

        await LoadSavedCredentialsForCurrentServerAsync();
    }

    partial void OnSelectedProtocolChanged(VpnProtocol value)
    {
        ConnectCommand.NotifyCanExecuteChanged();

        if (_selectedServer is { } entry)
        {
            ApplyEndpointForProtocol(entry, value);
        }

        if (!_applyingVpnGateSelection)
        {
            _ = LoadSavedCredentialsForCurrentServerAsync();
        }
    }

    private bool PromptForOpenVpnCredentialSource(out bool useProfileCredentials)
    {
        useProfileCredentials = false;
        if (SelectedProtocol != VpnProtocol.OpenVpn || !_isLocalOpenVpnProfile ||
            _localProfileAuthentication is not { } authentication)
        {
            return true;
        }

        var localCredentialsAvailable =
            !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
        var choice = _chooseOpenVpnCredentialSource(authentication, localCredentialsAvailable);
        if (choice is null)
        {
            return false;
        }

        useProfileCredentials = choice == OpenVpnCredentialSource.Profile;
        if (!useProfileCredentials && !localCredentialsAvailable)
        {
            ErrorMessage = "Preencha usuário e senha na janela principal para usar o login local.";
            return false;
        }

        return true;
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

    private void ResetCredentialTarget()
    {
        _lastCredentialServerKey = null;
        _credentialsWereLoadedFromStore = false;
        _usernameWasManuallyEntered = false;
        _passwordWasManuallyEntered = false;
        _manualPasswordServerKey = null;
        _manualPasswordValue = null;
    }

    private void SetCredentialsWithoutMarkingManual(string username, string password)
    {
        _applyingCredentials = true;
        try
        {
            Username = username;
            Password = password;
            _usernameWasManuallyEntered = false;
            _passwordWasManuallyEntered = false;
            _manualPasswordServerKey = null;
            _manualPasswordValue = null;
        }
        finally
        {
            _applyingCredentials = false;
        }
    }

    private string? CreateCredentialServerKey()
    {
        if (string.IsNullOrWhiteSpace(ServerHost) ||
            !int.TryParse(ServerPort, out var port) || port is < 1 or > 65535)
        {
            return null;
        }

        var host = ServerHost.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        return host.Length == 0 ? null : $"{SelectedProtocol}:{host}:{port}";
    }

    private async Task SaveCredentialsAfterSuccessfulConnectionAsync(bool credentialsWereUsed)
    {
        var serverKey = CreateCredentialServerKey();
        if (!credentialsWereUsed || !SaveCredentialsEnabled || serverKey is null ||
            (string.IsNullOrWhiteSpace(Username) && string.IsNullOrEmpty(_manualPasswordValue)))
        {
            return;
        }

        var manualPassword = _passwordWasManuallyEntered &&
                             string.Equals(_manualPasswordServerKey, serverKey, StringComparison.Ordinal)
            ? _manualPasswordValue
            : null;

        try
        {
            await _savedCredentialsUseCase.SaveAfterSuccessfulConnectionAsync(
                serverKey, Username, manualPassword);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao salvar credenciais do servidor {ServerKey}", serverKey);
        }
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

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            RefreshFromSession();
            return;
        }

        // A background cleanup can change the session while OnExit is blocking the UI
        // thread waiting for it. Queue the notification without waiting, otherwise the
        // cleanup and the dispatcher would deadlock each other.
        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(RefreshFromSession));
    }

    private void RefreshFromSession()
    {
        Status = _sessionContext.Status;
        StatusText = Status switch
        {
            ConnectionStatus.Idle => "Desconectado",
            ConnectionStatus.Connecting => "Conectando",
            ConnectionStatus.Connected => "Conectado",
            ConnectionStatus.Error => "Erro",
            _ => "Desconectado"
        };
        LatencyText = _sessionContext.Latency is { } latency ? $"{latency.TotalMilliseconds:F0} ms" : null;
        ErrorMessage = _sessionContext.LastError;

        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        VpnGateList.ServerSelected -= OnVpnGateServerSelected;
        _sessionContext.PropertyChanged -= OnSessionPropertyChanged;
    }
}

public sealed record ProcessPickerWindowResult(ProcessInfo Process);

public delegate string? BrowseForExecutable();

public delegate string? BrowseForOpenVpnProfile();

public delegate OpenVpnCredentialSource? ChooseOpenVpnCredentialSource(
    OpenVpnAuthenticationInfo authentication,
    bool localCredentialsAvailable);

public delegate bool ConfirmUpdatePrompt(Version currentVersion, UpdateReleaseInfo release);

public delegate void UpdateCheckMessage(string message);

public delegate void ExitForUpdate();
