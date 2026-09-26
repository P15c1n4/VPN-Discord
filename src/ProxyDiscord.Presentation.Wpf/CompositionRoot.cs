using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.Session;
using ProxyDiscord.Application.Vpn;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Infrastructure.Connectivity;
using ProxyDiscord.Infrastructure.OpenVpn;
using ProxyDiscord.Infrastructure.ProcessManagement;
using ProxyDiscord.Infrastructure.Ras;
using ProxyDiscord.Infrastructure.Routing;
using ProxyDiscord.Infrastructure.StateStore;
using ProxyDiscord.Infrastructure.Updates;
using ProxyDiscord.Infrastructure.VpnGate;
using ProxyDiscord.Infrastructure.WinDivert;
using ProxyDiscord.Presentation.Wpf.Logging;
using ProxyDiscord.Presentation.Wpf.ViewModels;
using ProxyDiscord.Presentation.Wpf.Views;

namespace ProxyDiscord.Presentation.Wpf;

internal static class CompositionRoot
{
    public static IServiceProvider Build(Dispatcher dispatcher)
    {
        var services = new ServiceCollection();
        var sessionContext = new RoutingSessionContext();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFilter("System.Net.Http", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);
            builder.AddProvider(new FileLoggerProvider(() => new Dictionary<string, object?>
            {
                ["status"] = sessionContext.Status.ToString(),
                ["targetProcessName"] = sessionContext.TargetProcess?.Name,
                ["targetProcessPid"] = sessionContext.TargetProcess?.Pid,
                ["latencyMilliseconds"] = sessionContext.Latency?.TotalMilliseconds,
                ["lastError"] = sessionContext.LastError,
            }));
        });

        services.AddSingleton(dispatcher);
        services.AddSingleton(sessionContext);

        services.AddProcessManagement();
        services.AddVpnGateIntegration();
        services.AddConnectivityTesting();
        services.AddSstpVpnManagement();
        services.AddOpenVpnManagement();
        services.AddProcessRouting();
        services.AddWinDivertPacketCapture();
        services.AddReleaseUpdates();
        services.AddConnectionStateStore();

        services.AddSingleton<TunnelDiagnostics>();
        services.AddSingleton<IVpnConnection, VpnConnectionRouter>();
        services.AddSingleton<RoutingSessionContext>(sessionContext);
        services.AddSingleton<IRoutingSessionContext>(sp => sp.GetRequiredService<RoutingSessionContext>());
        services.AddSingleton<DiscoverRunningProcessesUseCase>();
        services.AddSingleton<FetchVpnGateListUseCase>();
        services.AddSingleton<TestServerLatenciesUseCase>();
        services.AddSingleton<ConnectVpnUseCase>();
        services.AddSingleton<LoadOpenVpnProfileUseCase>();
        services.AddSingleton<DisconnectVpnUseCase>();
        services.AddSingleton<SavedCredentialsUseCase>();
        services.AddSingleton<VpnConnectionSupervisor>();
        services.AddSingleton<CleanupStaleStateOnStartupUseCase>();
        services.AddSingleton<CheckForUpdatesUseCase>();
        services.AddSingleton<LaunchUpdateUseCase>();

        services.AddSingleton<VpnGateListViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<DiagnosticsWindow>();
        services.AddTransient<ProcessPickerViewModel>();
        services.AddTransient<ProcessPickerWindow>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<Func<ProcessPickerWindowResult?>>(sp => () => OpenProcessPicker(sp));
        services.AddSingleton<BrowseForExecutable>(_ => PickExecutable);
        services.AddSingleton<BrowseForOpenVpnProfile>(_ => PickOpenVpnProfile);
        services.AddSingleton<ChooseOpenVpnCredentialSource>(_ => ChooseOpenVpnCredentialSourceDialog);
        services.AddSingleton<ConfirmUpdatePrompt>(_ => ShowUpdatePrompt);
        services.AddSingleton<UpdateCheckMessage>(_ => ShowUpdateCheckMessage);
        services.AddSingleton<ExitForUpdate>(_ => ExitApplicationForUpdate);
        services.AddSingleton<Action>(sp => () => ShowDiagnostics(sp));

        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<VpnConnectionSupervisor>();
        return provider;
    }

    private static void ShowDiagnostics(IServiceProvider serviceProvider)
    {
        var window = serviceProvider.GetRequiredService<DiagnosticsWindow>();
        if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.Show();
    }

    private static string? PickExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecione o executável do aplicativo",
            Filter = "Executáveis (*.exe)|*.exe|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PickOpenVpnProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecione um perfil OpenVPN",
            Filter = "Perfis OpenVPN (*.ovpn;*.conf)|*.ovpn;*.conf|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static OpenVpnCredentialSource? ChooseOpenVpnCredentialSourceDialog(
        OpenVpnAuthenticationInfo authentication,
        bool localCredentialsAvailable)
    {
        var window = new OpenVpnAuthenticationChoiceWindow(authentication, localCredentialsAvailable);
        if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true ? window.SelectedSource : null;
    }

    private static bool ShowUpdatePrompt(Version currentVersion, UpdateReleaseInfo release)
    {
        var window = new UpdateAvailableWindow(currentVersion, release);
        if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true && window.UpdateAccepted;
    }

    private static void ShowUpdateCheckMessage(string message) =>
        MessageBox.Show(
            System.Windows.Application.Current?.MainWindow,
            message,
            "Atualização do Discord-VPN",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private static void ExitApplicationForUpdate()
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ExitForUpdate();
        }
        else
        {
            System.Windows.Application.Current?.Shutdown();
        }
    }

    private static ProcessPickerWindowResult? OpenProcessPicker(IServiceProvider serviceProvider)
    {
        var window = serviceProvider.GetRequiredService<ProcessPickerWindow>();
        if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        var accepted = window.ShowDialog();
        return accepted == true && window.SelectedProcess is not null
            ? new ProcessPickerWindowResult(window.SelectedProcess)
            : null;
    }
}
