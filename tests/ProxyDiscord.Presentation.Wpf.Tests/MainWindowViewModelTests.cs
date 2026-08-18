using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Presentation.Wpf.ViewModels;

namespace ProxyDiscord.Presentation.Wpf.Tests;

public class MainWindowViewModelTests
{
    private static readonly ProcessInfo DISCORD_PROCESS = new(1234, "Discord", null);

    private readonly IVpnConnection _vpnConnection = Substitute.For<IVpnConnection>();
    private readonly IProcessRoutingEngine _routingEngine = Substitute.For<IProcessRoutingEngine>();
    private readonly IVpnRouteManager _routeManager = Substitute.For<IVpnRouteManager>();
    private readonly IVpnEgressSelfTest _egressSelfTest = Substitute.For<IVpnEgressSelfTest>();
    private readonly IConnectionStateStore _stateStore = Substitute.For<IConnectionStateStore>();
    private readonly IProcessLivenessChecker _livenessChecker = Substitute.For<IProcessLivenessChecker>();
    private readonly ISystemClock _clock = Substitute.For<ISystemClock>();
    private readonly IProcessRepository _processRepository = Substitute.For<IProcessRepository>();
    private readonly IVpnGateClient _vpnGateClient = Substitute.For<IVpnGateClient>();
    private readonly IPingService _pingService = Substitute.For<IPingService>();
    private readonly IOpenVpnProfileSource _openVpnProfileSource = Substitute.For<IOpenVpnProfileSource>();
    private readonly RoutingSessionContext _sessionContext = new();

    private MainWindowViewModel CreateViewModel(
        Func<ProcessPickerWindowResult?>? openPicker = null,
        BrowseForExecutable? browse = null,
        BrowseForOpenVpnProfile? browseProfile = null)
    {
        _livenessChecker.GetCurrentProcessInfo().Returns((Environment.ProcessId, DateTime.UtcNow));
        _clock.UtcNow.Returns(DateTime.UtcNow);
        _egressSelfTest.RunAsync(Arg.Any<VpnAdapterInfo>(), Arg.Any<CancellationToken>())
            .Returns(new Application.Diagnostics.EgressSelfTestResult(true, "ok", "203.0.113.9", "198.51.100.7", true));

        var connectUseCase = new ConnectVpnUseCase(
            _vpnConnection, _routingEngine, _routeManager, _egressSelfTest, _stateStore, _livenessChecker,
            _clock, _sessionContext, NullLogger<ConnectVpnUseCase>.Instance,
            trafficObservationTimeout: TimeSpan.FromMilliseconds(50));
        var disconnectUseCase = new DisconnectVpnUseCase(
            _routingEngine, _vpnConnection, _routeManager, _stateStore, _sessionContext,
            NullLogger<DisconnectVpnUseCase>.Instance);
        var discoverProcessesUseCase = new DiscoverRunningProcessesUseCase(_processRepository);
        var vpnGateList = new VpnGateListViewModel(
            new FetchVpnGateListUseCase(_vpnGateClient),
            new TestServerLatenciesUseCase(_pingService),
            Dispatcher.CurrentDispatcher,
            NullLogger<VpnGateListViewModel>.Instance);

        var loadProfileUseCase = new LoadOpenVpnProfileUseCase(
            _openVpnProfileSource, NullLogger<LoadOpenVpnProfileUseCase>.Instance);

        return new MainWindowViewModel(
            connectUseCase, disconnectUseCase, discoverProcessesUseCase, loadProfileUseCase, _sessionContext,
            vpnGateList, openPicker ?? (() => null), browse ?? (() => null), browseProfile ?? (() => null),
            () => { }, NullLogger<MainWindowViewModel>.Instance);
    }

    [Fact]
    public async Task InitializeAsync_DiscordRunning_SelectsItAsDefaultProcess()
    {
        _processRepository.GetRunningProcessesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProcessInfo> { DISCORD_PROCESS, new(1, "chrome", null) });
        var viewModel = CreateViewModel();

        await viewModel.InitializeAsync();

        Assert.Equal(DISCORD_PROCESS, viewModel.SelectedProcess);
        Assert.Contains("PID 1234", viewModel.SelectedProcessDisplay);
    }

    [Fact]
    public async Task InitializeAsync_DiscordNotRunning_LeavesSelectionEmptyWithClearMessage()
    {
        _processRepository.GetRunningProcessesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProcessInfo> { new(1, "chrome", null) });
        var viewModel = CreateViewModel();

        await viewModel.InitializeAsync();

        Assert.Null(viewModel.SelectedProcess);
        Assert.Contains("não está em execução", viewModel.SelectedProcessDisplay);
    }

    [Fact]
    public void CanConnect_NoProcessSelected_IsFalse()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public void OpenProcessPicker_UserSelectsAProcess_UpdatesSelection()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));

        viewModel.OpenProcessPickerCommand.Execute(null);

        Assert.Equal(DISCORD_PROCESS, viewModel.SelectedProcess);
    }

    [Fact]
    public void CanConnect_ProcessSelectedButNoServer_IsFalse()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);

        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public void CanConnect_ProcessAndServerBothSet_IsTrue()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);
        viewModel.ServerHost = "vpn.example.com";

        Assert.True(viewModel.CanConnect);
    }

    [Fact]
    public void CanConnect_ServerSetButNoProcess_IsFalse()
    {
        var viewModel = CreateViewModel();
        viewModel.ServerHost = "vpn.example.com";

        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public async Task ChangingTheTargetProcess_WhileConnected_TearsDownTheSession()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);
        viewModel.SelectedProtocol = VpnProtocol.Sstp;
        viewModel.ServerHost = "vpn.example.com";
        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(new VpnAdapterInfo("10.8.0.5", 1, 0));
        await viewModel.ConnectCommand.ExecuteAsync(null);
        Assert.True(viewModel.CanDisconnect);

        viewModel.OpenProcessPickerCommand.Execute(null);

        await _vpnConnection.Received().DisconnectAsync(Arg.Any<CancellationToken>());
        await _routingEngine.Received().StopAsync(Arg.Any<CancellationToken>());
        Assert.False(viewModel.CanDisconnect);
    }

    [Fact]
    public void BrowseForExecutable_ArmsTheTunnelForAnAppThatIsNotRunningYet()
    {
        var viewModel = CreateViewModel(browse: () => @"C:\Games\game.exe");

        viewModel.BrowseForExecutableCommand.Execute(null);

        Assert.NotNull(viewModel.SelectedProcess);
        Assert.Equal(@"C:\Games\game.exe", viewModel.SelectedProcess!.ExecutablePath);
        Assert.Equal("game", viewModel.SelectedProcess.Name);
        Assert.Contains("aguardando iniciar", viewModel.SelectedProcessDisplay);
    }

    [Fact]
    public void BrowseForExecutable_Cancelled_LeavesSelectionUnchanged()
    {
        var viewModel = CreateViewModel(browse: () => null);

        viewModel.BrowseForExecutableCommand.Execute(null);

        Assert.Null(viewModel.SelectedProcess);
    }

    [Fact]
    public async Task ConnectCommand_Success_DisablesConnectAndEnablesDisconnect()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);
        viewModel.SelectedProtocol = VpnProtocol.Sstp;
        viewModel.ServerHost = "vpn.example.com";
        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(new VpnAdapterInfo("10.8.0.5", 1, 0));

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.False(viewModel.CanConnect);
        Assert.True(viewModel.CanDisconnect);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task ConnectCommand_Failure_SurfacesErrorMessageAndKeepsConnectAvailable()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);
        viewModel.SelectedProtocol = VpnProtocol.Sstp;
        viewModel.ServerHost = "vpn.example.com";
        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Failed(VpnLinkStatus.Error, "senha incorreta"));

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("senha incorreta", viewModel.ErrorMessage);
        Assert.True(viewModel.CanConnect);
        Assert.False(viewModel.CanDisconnect);
    }

    [Fact]
    public async Task ConnectCommand_OpenVpnWithoutAServerFromTheList_RefusesBeforeDialling()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);
        viewModel.SelectedProtocol = VpnProtocol.OpenVpn;
        viewModel.ServerHost = "vpn.example.com";

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Contains("OpenVPN exige", viewModel.ErrorMessage);
        await _vpnConnection.DidNotReceive().ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void VpnGateServerSelected_WithBothProtocols_OffersBothAndDefaultsToOpenVpn()
    {
        var viewModel = CreateViewModel();

        viewModel.VpnGateList.SelectServerCommand.Execute(
            new VpnGateServerRowViewModel(MakeDualProtocolEntry("dual.opengw.net", 992)));

        Assert.Equal([VpnProtocol.OpenVpn, VpnProtocol.Sstp], viewModel.Protocols);
        Assert.Equal(VpnProtocol.OpenVpn, viewModel.SelectedProtocol);
        Assert.Equal("992", viewModel.ServerPort);
    }

    [Fact]
    public void SwitchingProtocol_OnASelectedServer_RepointsTheEndpoint()
    {
        var viewModel = CreateViewModel();
        viewModel.VpnGateList.SelectServerCommand.Execute(
            new VpnGateServerRowViewModel(MakeDualProtocolEntry("dual.opengw.net", 992)));

        viewModel.SelectedProtocol = VpnProtocol.Sstp;

        Assert.Equal("443", viewModel.ServerPort);
        Assert.Equal("dual.opengw.net", viewModel.ServerHost);
    }

    [Fact]
    public void VpnGateServerSelected_SstpOnlyServer_OffersOnlySstp()
    {
        var viewModel = CreateViewModel();

        viewModel.VpnGateList.SelectServerCommand.Execute(
            new VpnGateServerRowViewModel(MakeSstpOnlyEntry("sstponly.opengw.net")));

        Assert.Equal([VpnProtocol.Sstp], viewModel.Protocols);
        Assert.Equal(VpnProtocol.Sstp, viewModel.SelectedProtocol);
    }

    [Fact]
    public void VpnGateServerSelected_PrefillsHostAndPortAsSeparateFields()
    {
        var viewModel = CreateViewModel();
        var entry = MakeEntry("public-vpn-01.opengw.net", 992);

        viewModel.VpnGateList.SelectServerCommand.Execute(new VpnGateServerRowViewModel(entry));

        Assert.Equal("public-vpn-01.opengw.net", viewModel.ServerHost);
        Assert.Equal("992", viewModel.ServerPort);
    }

    [Fact]
    public async Task ConnectCommand_UsesTheSelectedServersOwnPort_NotAStaticDefault()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);
        viewModel.VpnGateList.SelectServerCommand.Execute(
            new VpnGateServerRowViewModel(MakeEntry("kanratown.opengw.net", 992)));

        VpnConnectionRequest? captured = null;
        _vpnConnection.ConnectAsync(Arg.Do<VpnConnectionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(new VpnAdapterInfo("10.8.0.5", 1, 0));

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal("kanratown.opengw.net", captured!.Endpoint.Host);
        Assert.Equal(992, captured.Endpoint.Port);
    }

    [Fact]
    public async Task ConnectCommand_EmptyPortField_FallsBackToTheProtocolDefault()
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);
        viewModel.SelectedProtocol = VpnProtocol.Sstp;
        viewModel.ServerHost = "vpn.example.com";
        viewModel.ServerPort = "";

        VpnConnectionRequest? captured = null;
        _vpnConnection.ConnectAsync(Arg.Do<VpnConnectionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(new VpnAdapterInfo("10.8.0.5", 1, 0));

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(443, captured!.Endpoint.Port);
    }

    [Fact]
    public void SettingServerHostToHostColonPort_SplitsItAcrossBothFields()
    {
        var viewModel = CreateViewModel();

        viewModel.ServerHost = "vpn115196132.opengw.net:1602";

        Assert.Equal("vpn115196132.opengw.net", viewModel.ServerHost);
        Assert.Equal("1602", viewModel.ServerPort);
    }

    private static readonly OpenVpnProfileDescriptor FILE_PROFILE = new(
        "meu-servidor.ovpn",
        @"C:\perfis\meu-servidor.ovpn",
        "Y29uZmln",
        HostEndpoint.Create("vpn.exemplo.com", 1195),
        TransportProtocol.Udp);

    [Fact]
    public async Task LoadOpenVpnProfile_ValidFile_FillsTheServerFieldsFromTheProfile()
    {
        _openVpnProfileSource.LoadAsync(FILE_PROFILE.FilePath, Arg.Any<CancellationToken>()).Returns(FILE_PROFILE);
        var viewModel = CreateViewModel(browseProfile: () => FILE_PROFILE.FilePath);

        await viewModel.LoadOpenVpnProfileCommand.ExecuteAsync(null);

        Assert.Equal("vpn.exemplo.com", viewModel.ServerHost);
        Assert.Equal("1195", viewModel.ServerPort);
        Assert.Equal(VpnProtocol.OpenVpn, viewModel.SelectedProtocol);
        Assert.Contains("meu-servidor.ovpn", viewModel.OpenVpnProfileSource);
        Assert.Contains("UDP", viewModel.OpenVpnProfileSource);
    }

    [Fact]
    public async Task LoadOpenVpnProfile_ValidFile_LeavesOpenVpnAsTheOnlyProtocol()
    {
        _openVpnProfileSource.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FILE_PROFILE);
        var viewModel = CreateViewModel(browseProfile: () => FILE_PROFILE.FilePath);

        await viewModel.LoadOpenVpnProfileCommand.ExecuteAsync(null);

        Assert.Equal(new[] { VpnProtocol.OpenVpn }, viewModel.Protocols.ToArray());
    }

    [Fact]
    public async Task LoadOpenVpnProfile_ValidFile_IsTheProfileHandedToTheConnection()
    {
        _openVpnProfileSource.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FILE_PROFILE);
        var viewModel = CreateViewModel(
            openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS),
            browseProfile: () => FILE_PROFILE.FilePath);
        viewModel.OpenProcessPickerCommand.Execute(null);
        await viewModel.LoadOpenVpnProfileCommand.ExecuteAsync(null);

        VpnConnectionRequest? captured = null;
        _vpnConnection.ConnectAsync(Arg.Do<VpnConnectionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(new VpnAdapterInfo("10.8.0.5", 1, 0));

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Y29uZmln", captured!.OpenVpnConfigBase64);
        Assert.Equal("vpn.exemplo.com", captured.Endpoint.Host);
        Assert.Equal(1195, captured.Endpoint.Port);
    }

    [Fact]
    public async Task LoadOpenVpnProfile_AfterPickingAServer_ReplacesThatSelection()
    {
        _openVpnProfileSource.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FILE_PROFILE);
        var viewModel = CreateViewModel(browseProfile: () => FILE_PROFILE.FilePath);
        viewModel.VpnGateList.SelectServerCommand.Execute(
            new VpnGateServerRowViewModel(MakeEntry("vpn123.opengw.net", 1234)));

        await viewModel.LoadOpenVpnProfileCommand.ExecuteAsync(null);
        viewModel.SelectedProtocol = VpnProtocol.OpenVpn;

        Assert.Equal("vpn.exemplo.com", viewModel.ServerHost);
    }

    [Fact]
    public async Task LoadOpenVpnProfile_RejectedFile_ShowsTheReasonAndKeepsNoProfile()
    {
        _openVpnProfileSource.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("não contém uma diretiva 'remote' válida"));
        var viewModel = CreateViewModel(
            openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS),
            browseProfile: () => @"C:\perfis\quebrado.ovpn");
        viewModel.OpenProcessPickerCommand.Execute(null);

        await viewModel.LoadOpenVpnProfileCommand.ExecuteAsync(null);

        Assert.Contains("remote", viewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", viewModel.OpenVpnProfileSource);

        viewModel.ServerHost = "vpn.exemplo.com";
        await viewModel.ConnectCommand.ExecuteAsync(null);
        await _vpnConnection.DidNotReceive().ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadOpenVpnProfile_UserCancelsTheDialog_ChangesNothing()
    {
        var viewModel = CreateViewModel(browseProfile: () => null);

        await viewModel.LoadOpenVpnProfileCommand.ExecuteAsync(null);

        Assert.Equal("", viewModel.OpenVpnProfileSource);
        await _openVpnProfileSource.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ProtocolScope_DefaultsToTcpOnly()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(TunnelProtocolScope.TcpOnly, viewModel.SelectedProtocolScope);
        Assert.Equal(
            new[] { TunnelProtocolScope.TcpOnly, TunnelProtocolScope.UdpOnly, TunnelProtocolScope.TcpAndUdp },
            viewModel.ProtocolScopes.ToArray());
    }

    [Theory]
    [InlineData(TunnelProtocolScope.TcpOnly)]
    [InlineData(TunnelProtocolScope.UdpOnly)]
    [InlineData(TunnelProtocolScope.TcpAndUdp)]
    public async Task ConnectCommand_PassesTheSelectedScopeToTheRoutingEngine(TunnelProtocolScope scope)
    {
        var viewModel = CreateViewModel(openPicker: () => new ProcessPickerWindowResult(DISCORD_PROCESS));
        viewModel.OpenProcessPickerCommand.Execute(null);
        viewModel.SelectedProtocol = VpnProtocol.Sstp;
        viewModel.ServerHost = "vpn.example.com";
        viewModel.SelectedProtocolScope = scope;

        _vpnConnection.ConnectAsync(Arg.Any<VpnConnectionRequest>(), Arg.Any<CancellationToken>())
            .Returns(VpnConnectionResult.Ok(VpnLinkStatus.Connected));
        _vpnConnection.GetAdapterInfoAsync(Arg.Any<CancellationToken>()).Returns(new VpnAdapterInfo("10.8.0.5", 1, 0));

        await viewModel.ConnectCommand.ExecuteAsync(null);

        await _routingEngine.Received(1).StartAsync(
            Arg.Any<TargetProcessSelector>(), Arg.Any<VpnAdapterInfo>(), Arg.Any<TunnelDnsSettings>(),
            scope, Arg.Any<CancellationToken>());
    }

    private static VpnGateServerEntry MakeEntry(string host, int port) => new(
        HostName: host.Replace(".opengw.net", ""),
        IpAddress: "1.2.3.4",
        OpenVpnEndpoint: HostEndpoint.Create(host, port),
        OpenVpnTransport: TransportProtocol.Tcp,
        SstpEndpoint: null,
        OpenVpnConfigBase64: "Zm9v",
        Score: 1000, PingMs: 10, SpeedBps: 100,
        CountryLong: "Japan", CountryShort: "JP", NumVpnSessions: 5, Uptime: 100,
        TotalUsers: 10, TotalTraffic: 100, Operator: "op", Message: "");

    private static VpnGateServerEntry MakeDualProtocolEntry(string host, int openVpnPort) => MakeEntry(host, openVpnPort)
        with { SstpEndpoint = HostEndpoint.Create(host, 443) };

    private static VpnGateServerEntry MakeSstpOnlyEntry(string host) => MakeEntry(host, 443) with
    {
        OpenVpnEndpoint = null,
        OpenVpnTransport = null,
        OpenVpnConfigBase64 = "",
        SstpEndpoint = HostEndpoint.Create(host, 443),
    };
}
