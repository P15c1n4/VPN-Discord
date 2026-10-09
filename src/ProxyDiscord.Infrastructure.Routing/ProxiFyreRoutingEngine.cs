using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class ProxiFyreRoutingEngine : IProcessRoutingEngine
{
    private static readonly TimeSpan STARTUP_CHECK_DELAY = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan STOP_TIMEOUT = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly LocalSocks5VpnEndpoint socksEndpoint;
    private readonly TunnelDiagnostics diagnostics;
    private readonly ILogger<ProxiFyreRoutingEngine> logger;
    private Process? _process;
    private volatile bool _stopping;

    internal ProxiFyreRoutingEngine(
        LocalSocks5VpnEndpoint socksEndpoint,
        TunnelDiagnostics diagnostics,
        ILogger<ProxiFyreRoutingEngine> logger)
    {
        this.socksEndpoint = socksEndpoint;
        this.diagnostics = diagnostics;
        this.logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };
    public event EventHandler? TrafficObserved;
    public event EventHandler<RoutingEngineFailureEventArgs>? Failed;

    public async Task StartAsync(
        TargetProcessSelector target,
        VpnAdapterInfo vpnAdapter,
        TunnelDnsSettings dnsSettings,
        TunnelProtocolScope scope = TunnelProtocolScope.TcpAndUdp,
        ProcessRoutingBackend backend = ProcessRoutingBackend.ProxiFyre,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (backend != ProcessRoutingBackend.ProxiFyre)
            {
                throw new InvalidOperationException("O motor ProxiFyre recebeu uma solicitação para outro backend.");
            }

            if (_process is not null)
            {
                throw new InvalidOperationException("O motor ProxiFyre já está ativo.");
            }

            var directory = GetRuntimeDirectory();
            var executable = Path.Combine(directory, "ProxiFyre.exe");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException(
                    "ProxiFyre não foi encontrado. Coloque os arquivos x64 da versão compatível em proxifyre\\managed ao lado do aplicativo.",
                    executable);
            }

            EnsureNoExistingProxiFyreProcess();

            Directory.CreateDirectory(Path.Combine(directory, "logs"));
            try
            {
                diagnostics.Reset();
                diagnostics.SetBackend(ProcessRoutingBackend.ProxiFyre);
                diagnostics.SetScope(scope);
                socksEndpoint.Start(vpnAdapter, dnsSettings);
                await WriteConfigurationAsync(directory, executable, target, scope, cancellationToken);
                _process = HiddenConsoleProcess.Start(executable, directory);
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;
                await Task.Delay(STARTUP_CHECK_DELAY, cancellationToken);
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"O ProxiFyre encerrou durante a inicialização (código {_process.ExitCode}). {ReadLatestLog(directory)}");
                }
            }
            catch
            {
                await StopCoreAsync();
                throw;
            }

            socksEndpoint.TrafficObserved += OnTrafficObserved;
            diagnostics.SetRoutingEngineRunning(true);
            diagnostics.Note($"Roteamento por processo ativo com ProxiFyre; escopo {TunnelDiagnostics.DescribeScope(scope)}.");
            logger.LogInformation("ProxiFyre iniciado para {Target}; SOCKS5 local em 127.0.0.1:{Port}; IPv4", target.DisplayName, socksEndpoint.Port);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }

    private async Task StopCoreAsync()
    {
        socksEndpoint.TrafficObserved -= OnTrafficObserved;
        var process = _process;
        _stopping = process is not null;
        try
        {
            await socksEndpoint.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao encerrar o endpoint SOCKS5; o processo ProxiFyre ainda será parado");
        }

        if (process is null)
        {
            return;
        }

        process.Exited -= OnProcessExited;
        try
        {
            if (!process.HasExited)
            {
                if (!HiddenConsoleProcess.SendCtrlBreak(process.Id))
                {
                    logger.LogWarning("Não foi possível enviar Ctrl+Break ao ProxiFyre; aguardando o encerramento do processo.");
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(STOP_TIMEOUT);
                }
                catch (TimeoutException)
                {
                    logger.LogError("ProxiFyre não encerrou em {Timeout}s; encerrando o processo para liberar a sessão.", STOP_TIMEOUT.TotalSeconds);
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                }
            }
        }
        finally
        {
            if (process.HasExited)
            {
                process.Dispose();
                _process = null;
                diagnostics.SetRoutingEngineRunning(false);
                diagnostics.Note("Roteamento por processo ProxiFyre encerrado.");
            }
            else
            {
                logger.LogCritical("O processo ProxiFyre continua ativo depois da tentativa de encerramento.");
            }

            _stopping = false;
        }
    }

    private async Task WriteConfigurationAsync(
        string directory,
        string executable,
        TargetProcessSelector target,
        TunnelProtocolScope scope,
        CancellationToken cancellationToken)
    {
        var targetName = string.IsNullOrWhiteSpace(target.ExecutablePath)
            ? target.ProcessName
            : Path.GetFileName(target.ExecutablePath);
        var targetPath = string.IsNullOrWhiteSpace(target.ExecutablePath)
            ? targetName
            : Path.GetFullPath(target.ExecutablePath);
        var targetMatch = string.IsNullOrWhiteSpace(target.ExecutablePath) ? targetName : targetPath;
        var appPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "Discord-VPN.exe";
        var protocols = scope switch
        {
            TunnelProtocolScope.TcpOnly => new[] { "TCP" },
            TunnelProtocolScope.UdpOnly => new[] { "UDP" },
            _ => new[] { "TCP", "UDP" },
        };

        var configuration = new
        {
            logLevel = "Info",
            bypassLan = false,
            proxies = new[]
            {
                new
                {
                    appNames = new[] { targetMatch },
                    socks5ProxyEndpoint = $"127.0.0.1:{socksEndpoint.Port}",
                    supportedProtocols = protocols,
                    supportedAddressFamilies = new[] { "IPv4" },
                },
            },
            excludes = new[] { appPath, executable, "openvpn.exe" },
        };

        var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(directory, "app-config.json");
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string GetRuntimeDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "proxifyre", "managed");

    private static void EnsureNoExistingProxiFyreProcess()
    {
        var existing = Process.GetProcessesByName("ProxiFyre");
        try
        {
            if (existing.Length > 0)
            {
                throw new InvalidOperationException(
                    "Já existe outro processo ProxiFyre em execução. Encerre essa instância antes de usar o motor integrado.");
            }
        }
        finally
        {
            foreach (var process in existing) process.Dispose();
        }
    }

    private static string ReadLatestLog(string directory)
    {
        try
        {
            var log = Directory.GetFiles(Path.Combine(directory, "logs"), "*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (log is null) return "Consulte os logs em proxifyre\\managed\\logs.";
            return string.Join(" | ", File.ReadLines(log.FullName).TakeLast(8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Não foi possível ler o log do ProxiFyre: {ex.Message}";
        }
    }

    private void OnTrafficObserved(object? sender, EventArgs args) => TrafficObserved?.Invoke(this, EventArgs.Empty);

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (_stopping) return;
        var reason = "O processo ProxiFyre encerrou inesperadamente.";
        diagnostics.SetRoutingEngineRunning(false);
        diagnostics.Note(reason, DiagnosticSeverity.Error);
        logger.LogError(reason);
        Failed?.Invoke(this, new RoutingEngineFailureEventArgs(reason));
    }

}
