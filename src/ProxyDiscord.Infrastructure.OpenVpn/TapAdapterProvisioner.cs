using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.OpenVpn;

internal sealed class TapAdapterProvisioner(OpenVpnBinaries binaries, ILogger<TapAdapterProvisioner> logger)
{
    public const string ADAPTER_NAME = "Discord-VPN Tunnel";

    // O adaptador aparece em "Conexões de rede" do Windows, então segue o nome do app. O nome antigo
    // continua sendo aceito: quem já rodou a versão anterior tem um adaptador TAP instalado, e criar
    // outro só porque o nome mudou deixaria dois adaptadores permanentes na máquina.
    private const string LEGACY_ADAPTER_NAME = "ProxyDiscord Tunnel";

    private const string HARDWARE_ID = "tap0901";

    private static readonly TimeSpan TOOL_TIMEOUT = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ADAPTER_DISCOVERY_TIMEOUT = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ADAPTER_DISCOVERY_INTERVAL = TimeSpan.FromMilliseconds(250);
    private const int PNPUTIL_NO_MATCHING_DEVICE = 259;
    private const int PNPUTIL_REBOOT_REQUIRED = 3010;

    public async Task<string> EnsureAdapterAsync(CancellationToken cancellationToken = default)
    {
        if (FindExistingAdapter() is { } existing)
        {
            logger.LogDebug("Adaptador TAP '{Name}' já existe; reutilizando.", existing);
            return existing;
        }

        await InstallDriverAsync(cancellationToken);
        await CreateAdapterAsync(cancellationToken);

        var createdAdapter = await WaitForAdapterAsync(cancellationToken);
        if (createdAdapter is null)
        {
            throw new InvalidOperationException(
                $"O tapctl concluiu a criação do adaptador TAP '{ADAPTER_NAME}', mas o Windows não o listou " +
                $"em até {ADAPTER_DISCOVERY_TIMEOUT.TotalSeconds:0} segundos.");
        }

        logger.LogInformation("Adaptador TAP '{Name}' criado e reconhecido pelo Windows.", createdAdapter);
        return createdAdapter;
    }

    internal static string? FindExistingAdapter()
    {
        var present = NetworkInterface.GetAllNetworkInterfaces();
        return new[] { ADAPTER_NAME, LEGACY_ADAPTER_NAME }.FirstOrDefault(
            name => present.Any(nic => string.Equals(nic.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task InstallDriverAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Instalando o driver tap-windows6 a partir de {Inf}", binaries.DriverInf);

        var result = await ProcessRunner.RunAsync(
            Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
            ["/add-driver", binaries.DriverInf, "/install"],
            TOOL_TIMEOUT,
            cancellationToken);

        if (!result.Success && result.ExitCode is not PNPUTIL_NO_MATCHING_DEVICE and not PNPUTIL_REBOOT_REQUIRED)
        {
            throw new InvalidOperationException(
                $"Não foi possível instalar o driver TAP (pnputil, código {result.ExitCode}). " +
                $"Detalhes: {result.Output.Trim()}");
        }

        if (result.ExitCode == PNPUTIL_REBOOT_REQUIRED)
        {
            logger.LogWarning(
                "O pnputil instalou o driver TAP, mas o Windows indicou que uma reinicialização será necessária " +
                "para concluir a instalação. Tentando criar o adaptador nesta sessão.");
        }

        logger.LogDebug("pnputil: {Output}", result.Output.Trim());
    }

    private static async Task<string?> WaitForAdapterAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ADAPTER_DISCOVERY_TIMEOUT;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (FindExistingAdapter() is { } adapter)
            {
                return adapter;
            }

            await Task.Delay(ADAPTER_DISCOVERY_INTERVAL, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return FindExistingAdapter();
    }

    private async Task CreateAdapterAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            binaries.TapCtlExe,
            ["create", "--hwid", HARDWARE_ID, "--name", ADAPTER_NAME],
            TOOL_TIMEOUT,
            cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Não foi possível criar o adaptador TAP (tapctl, código {result.ExitCode}). " +
                $"Detalhes: {result.Output.Trim()}");
        }
    }

    public async Task RemoveAdapterAsync(CancellationToken cancellationToken = default)
    {
        if (FindExistingAdapter() is not { } name)
        {
            return;
        }

        var result = await ProcessRunner.RunAsync(
            binaries.TapCtlExe, ["delete", name], TOOL_TIMEOUT, cancellationToken);

        if (!result.Success)
        {
            logger.LogWarning(
                "Falha ao remover o adaptador TAP '{Name}' (tapctl saiu com {Code}): {Output}",
                name, result.ExitCode, result.Output.Trim());
        }
    }
}
