using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Application.Vpn;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Infrastructure.OpenVpn;
using ProxyDiscord.Infrastructure.StateStore;
using ProxyDiscord.Presentation.Wpf.ViewModels;

namespace ProxyDiscord.Application.Tests;

internal sealed class MisuseTestEnvironment : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"ProxyDiscord-Misuse-{Guid.NewGuid():N}");
    public RoutingSessionContext Session { get; } = new();
    public SimulatedProvider OpenVpn { get; } = new(VpnProtocol.OpenVpn);
    public SimulatedProvider Sstp { get; } = new(VpnProtocol.Sstp);
    public SimulatedRouting Routing { get; } = new();
    public SimulatedStages Stages { get; } = new();
    public JsonApplicationDataStore Store { get; }
    public SavedCredentialsUseCase Credentials { get; }
    public ConnectVpnUseCase Connect { get; }
    public DisconnectVpnUseCase Disconnect { get; }
    public LoadOpenVpnProfileUseCase LoadProfile { get; } = new(new LocalOpenVpnProfileSource(), NullLogger<LoadOpenVpnProfileUseCase>.Instance);

    public MisuseTestEnvironment()
    {
        Directory.CreateDirectory(DirectoryPath);
        Store = new JsonApplicationDataStore(NullLogger<JsonApplicationDataStore>.Instance, DirectoryPath,
            value => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)),
            value => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)));
        Credentials = new SavedCredentialsUseCase(Store, Store);
        var router = new VpnConnectionRouter([OpenVpn, Sstp], NullLogger<VpnConnectionRouter>.Instance);
        Connect = new ConnectVpnUseCase(router, Routing, Stages, Stages, Stages, Stages, Stages, Stages,
            Session, NullLogger<ConnectVpnUseCase>.Instance, TimeSpan.Zero);
        Disconnect = new DisconnectVpnUseCase(Routing, router, Stages, Stages, Session,
            NullLogger<DisconnectVpnUseCase>.Instance);
    }

    public static ConnectVpnCommand Command(VpnProtocol protocol = VpnProtocol.OpenVpn) =>
        new(new ProcessInfo(123, "discord", null), "vpn.example:443", protocol, "alice", "valid-secret", "profile");

    public MainWindowViewModel CreateViewModel(Func<string?>? browseProfile = null,
        OpenVpnCredentialSource? credentialSource = OpenVpnCredentialSource.Local)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        return new MainWindowViewModel(Connect, Disconnect, null!, LoadProfile, Credentials, Session,
            new VpnGateListViewModel(null!, null!, dispatcher, NullLogger<VpnGateListViewModel>.Instance),
            () => null, () => null, () => browseProfile?.Invoke(), (_, _) => credentialSource,
            null!, null!, (_, _) => false, _ => { }, () => { }, () => { }, dispatcher,
            NullLogger<MainWindowViewModel>.Instance)
        {
            SelectedProcess = new ProcessInfo(123, "discord", null), ServerHost = "vpn.example", ServerPort = "443"
        };
    }

    public async Task<string> WriteProfileAsync(string content)
    {
        var path = Path.Combine(DirectoryPath, $"profile-{Guid.NewGuid():N}.ovpn");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    public JsonApplicationDataStore ReopenStore() => new(NullLogger<JsonApplicationDataStore>.Instance, DirectoryPath,
        value => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)),
        value => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)));

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);

    internal sealed class PausePoint
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }

    internal sealed class SimulatedProvider(VpnProtocol protocol) : IVpnProvider
    {
        public VpnProtocol Protocol => protocol;
        public event EventHandler<VpnConnectionLostEventArgs>? ConnectionLost { add { } remove { } }
        public PausePoint? DialPause { get; set; }
        public PausePoint? AdapterPause { get; set; }
        public bool RejectAuthentication { get; set; }
        public bool MissingAdapter { get; set; }
        public bool Active { get; private set; }
        public int ConnectCount { get; private set; }
        public VpnConnectionRequest? LastRequest { get; private set; }
        public async Task<VpnConnectionResult> ConnectAsync(VpnConnectionRequest request, CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            LastRequest = request;
            if (DialPause is not null) await DialPause.WaitAsync(cancellationToken);
            if (RejectAuthentication) return VpnConnectionResult.Failed(VpnLinkStatus.Error, "Autenticação recusada (simulação).");
            Active = true;
            return VpnConnectionResult.Ok(VpnLinkStatus.Connected);
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { Active = false; return Task.CompletedTask; }
        public Task ForceDisconnectByNameAsync(string name, CancellationToken cancellationToken = default) => DisconnectAsync(cancellationToken);
        public Task<VpnLinkStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Active ? VpnLinkStatus.Connected : VpnLinkStatus.Disconnected);
        public async Task<VpnAdapterInfo?> GetAdapterInfoAsync(CancellationToken cancellationToken = default)
        {
            if (AdapterPause is not null) await AdapterPause.WaitAsync(cancellationToken);
            return MissingAdapter ? null : new VpnAdapterInfo("10.8.0.2", 9, 0, "10.8.0.1");
        }
    }

    internal sealed class SimulatedRouting : IProcessRoutingEngine
    {
        public event EventHandler? TrafficObserved { add { } remove { } }
        public event EventHandler<RoutingEngineFailureEventArgs>? Failed { add { } remove { } }
        public bool IsRunning { get; private set; }
        public PausePoint? StartPause { get; set; }
        public bool FailStartup { get; set; }
        public async Task StartAsync(TargetProcessSelector target, VpnAdapterInfo adapter, TunnelDnsSettings dns,
            TunnelProtocolScope scope = TunnelProtocolScope.TcpAndUdp, ProcessRoutingBackend backend = ProcessRoutingBackend.WinDivert,
            CancellationToken cancellationToken = default)
        {
            if (StartPause is not null) await StartPause.WaitAsync(cancellationToken);
            if (FailStartup) throw new IOException("Motor indisponível (simulação).");
            IsRunning = true;
        }
        public Task StopAsync(CancellationToken cancellationToken = default) { IsRunning = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class SimulatedStages : IVpnRouteManager, IVpnEgressSelfTest, IOutboundInterfaceResolver,
        IConnectionStateStore, IProcessLivenessChecker, ISystemClock
    {
        public PausePoint? PhysicalPause { get; set; }
        public PausePoint? EgressPause { get; set; }
        public PausePoint? StatePause { get; set; }
        public bool FailEgress { get; set; }
        public bool FailRoute { get; set; }
        public bool HasRoute { get; private set; }
        public ConnectionStateRecord? State { get; private set; }
        public DateTime UtcNow => DateTime.UtcNow;
        public void EnsureTunnelDefaultRoute(VpnAdapterInfo adapter)
        {
            if (FailRoute) throw new IOException("Rota indisponível (simulação).");
            HasRoute = true;
        }
        public void RemoveTunnelDefaultRoute() => HasRoute = false;
        public int RemoveOrphanedRoutes() => 0;
        public async Task<EgressSelfTestResult> RunAsync(VpnAdapterInfo adapter, OutboundInterfaceInfo? direct = null, CancellationToken cancellationToken = default)
        {
            if (EgressPause is not null) await EgressPause.WaitAsync(cancellationToken);
            return new EgressSelfTestResult(!FailEgress, "Teste de saída simulado.");
        }
        public async Task<OutboundInterfaceInfo?> CapturePhysicalInterfaceAsync(CancellationToken cancellationToken = default)
        {
            if (PhysicalPause is not null) await PhysicalPause.WaitAsync(cancellationToken);
            return null;
        }
        public async Task WriteActiveStateAsync(ConnectionStateRecord record, CancellationToken cancellationToken = default)
        {
            if (StatePause is not null) await StatePause.WaitAsync(cancellationToken);
            State = record;
        }
        public Task ClearStateAsync(CancellationToken cancellationToken = default) { State = null; return Task.CompletedTask; }
        public Task<ConnectionStateRecord?> TryReadStaleStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public (int Pid, DateTime StartedUtc) GetCurrentProcessInfo() => (1, DateTime.UtcNow);
        public bool IsSameProcessStillRunning(int pid, DateTime start) => true;
    }
}

internal static class StaTest
{
    public static Task RunAsync(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception ex) { finished.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
