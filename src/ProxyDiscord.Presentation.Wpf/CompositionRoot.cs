using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
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

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFilter("System.Net.Http", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);
            builder.AddProvider(new FileLoggerProvider());
        });

        services.AddSingleton(dispatcher);

        services.AddProcessManagement();
        services.AddVpnGateIntegration();
        services.AddConnectivityTesting();
        services.AddSstpVpnManagement();
        services.AddOpenVpnManagement();
        services.AddProcessRouting();
        services.AddWinDivertPacketCapture();
        services.AddConnectionStateStore();

        services.AddSingleton<TunnelDiagnostics>();
        services.AddSingleton<IVpnConnection, VpnConnectionRouter>();
        services.AddSingleton<RoutingSessionContext>();
        services.AddSingleton<IRoutingSessionContext>(sp => sp.GetRequiredService<RoutingSessionContext>());
        services.AddSingleton<DiscoverRunningProcessesUseCase>();
        services.AddSingleton<FetchVpnGateListUseCase>();
        services.AddSingleton<TestServerLatenciesUseCase>();
        services.AddSingleton<ConnectVpnUseCase>();
        services.AddSingleton<LoadOpenVpnProfileUseCase>();
        services.AddSingleton<DisconnectVpnUseCase>();
        services.AddSingleton<CleanupStaleStateOnStartupUseCase>();

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
        services.AddSingleton<Action>(sp => () => ShowDiagnostics(sp));

        return services.BuildServiceProvider();
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
            Title = "Selecione o executável a ser tunelado",
            Filter = "Executáveis (*.exe)|*.exe|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PickOpenVpnProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecione o arquivo de configuração OpenVPN",
            Filter = "Perfil OpenVPN (*.ovpn;*.conf)|*.ovpn;*.conf|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
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
