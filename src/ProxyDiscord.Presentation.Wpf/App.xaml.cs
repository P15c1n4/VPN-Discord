using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Presentation.Wpf.Shell;
using ProxyDiscord.Presentation.Wpf.Views;

namespace ProxyDiscord.Presentation.Wpf;

public partial class App : System.Windows.Application
{
    private const string TRAY_TOOLTIP = "Discord-VPN";

    // A limpeza tem que caber no tempo que cada caminho de saída nos dá. Saída deliberada pode
    // esperar o desligamento gracioso do OpenVPN (8s) mais a remoção da entrada RAS; um handler de
    // crash ou de fim de sessão do Windows, não — ali o orçamento curto é o que garante que algo
    // seja feito antes de o processo morrer.
    private static readonly TimeSpan EXIT_CLEANUP_BUDGET = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ABRUPT_CLEANUP_BUDGET = TimeSpan.FromSeconds(5);

    private readonly object _cleanupLock = new();

    private IServiceProvider? _services;
    private Views.MainWindow? _mainWindow;
    private ILogger<App>? _logger;
    private SingleInstanceGuard? _singleInstance;
    private TrayIcon? _trayIcon;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.IsPrimary)
        {
            SingleInstanceGuard.TrySignalRunningInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _singleInstance.ActivationRequested += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _singleInstance.StartListening();

        _services = CompositionRoot.Build(Dispatcher);
        _logger = _services.GetRequiredService<ILogger<App>>();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RunCleanup($"AppDomain.UnhandledException: {args.ExceptionObject}", ABRUPT_CLEANUP_BUDGET);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RunCleanup("ProcessExit", ABRUPT_CLEANUP_BUDGET);
        DispatcherUnhandledException += (_, args) =>
            RunCleanup($"DispatcherUnhandledException: {args.Exception}", ABRUPT_CLEANUP_BUDGET);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            RunCleanup($"UnobservedTaskException: {args.Exception}", ABRUPT_CLEANUP_BUDGET);
            args.SetObserved();
        };

        // Logoff/desligamento do Windows: a janela pode estar escondida na bandeja, e ninguém vai
        // clicar em nada. Desfaz o túnel aqui, enquanto o sistema ainda espera pelo processo.
        SessionEnding += (_, args) =>
        {
            _logger?.LogInformation("Sessão do Windows terminando ({Reason}); desfazendo o túnel.", args.ReasonSessionEnding);
            RunCleanup($"SessionEnding: {args.ReasonSessionEnding}", ABRUPT_CLEANUP_BUDGET);
            ExitApplication();
        };

        try
        {
            await _services.GetRequiredService<CleanupStaleStateOnStartupUseCase>().ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao executar a limpeza de estado órfão na inicialização");
        }

        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        _mainWindow = mainWindow;

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        mainWindow.Closed += (_, _) => Shutdown();

        CreateTrayIcon();
        mainWindow.Show();
    }

    private void CreateTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon(TRAY_TOOLTIP);
            _trayIcon.OpenRequested += (_, _) => Dispatcher.Invoke(ShowMainWindow);
            _trayIcon.ExitRequested += (_, _) => Dispatcher.Invoke(ExitApplication);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Falha ao criar o ícone da área de notificação");
        }
    }

    // A janela cancela o próprio fechamento para ir parar na área de notificação (o X), então o
    // único caminho de saída precisa liberá-la antes de derrubar o app. Fechar a janela é o que
    // dispara o Shutdown (via o handler de Closed), para haver um único ponto de encerramento.
    private void ExitApplication()
    {
        if (_mainWindow is { } window)
        {
            window.AllowClose();
            window.Close();
            return;
        }

        Shutdown();
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        window.Show();

        if (window.WindowState == System.Windows.WindowState.Minimized)
        {
            window.WindowState = System.Windows.WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        RunCleanup("OnExit", EXIT_CLEANUP_BUDGET);

        _trayIcon?.Dispose();
        _trayIcon = null;
        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }

    // Desfaz VPN, rota e motor de roteamento. Chamável quantas vezes for preciso — o
    // DisconnectVpnUseCase é idempotente por construção (§2.4) e o lock só impede que dois
    // handlers rodem a limpeza ao mesmo tempo. Um guard de execução única seria pior: um
    // UnobservedTaskException qualquer no meio da sessão o consumiria, e a saída de verdade
    // sairia sem limpar nada.
    private void RunCleanup(string reason, TimeSpan budget)
    {
        if (_services is null)
        {
            return;
        }

        lock (_cleanupLock)
        {
            _logger?.LogWarning("Executando limpeza ({Reason})", reason);

            try
            {
                // Task.Run tira a continuação do SynchronizationContext do dispatcher: este método
                // roda na thread da UI e bloqueia nela, então uma continuação agendada de volta
                // para o dispatcher travaria até o orçamento estourar — e a limpeza não aconteceria.
                var task = System.Threading.Tasks.Task.Run(
                    () => _services.GetRequiredService<DisconnectVpnUseCase>().ExecuteAsync());

                if (!task.Wait(budget))
                {
                    _logger?.LogError(
                        "A limpeza não terminou em {Seconds}s ({Reason}); pode ter sobrado VPN, rota ou processo do OpenVPN — a próxima execução limpa o que ficou.",
                        budget.TotalSeconds, reason);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Falha durante a limpeza");
            }
        }
    }
}
