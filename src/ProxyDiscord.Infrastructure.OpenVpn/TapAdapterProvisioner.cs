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

    public async Task<string> EnsureAdapterAsync(CancellationToken cancellationToken = default)
    {
        if (FindExistingAdapter() is { } existing)
        {
            logger.LogDebug("Adaptador TAP '{Name}' já existe; reutilizando.", existing);
            return existing;
        }

        await InstallDriverAsync(cancellationToken);
        await CreateAdapterAsync(cancellationToken);

        if (FindExistingAdapter() is null)
        {
            throw new InvalidOperationException(
                $"O adaptador TAP '{ADAPTER_NAME}' foi criado, mas não apareceu na lista de interfaces de rede.");
        }

        logger.LogInformation("Adaptador TAP '{Name}' criado.", ADAPTER_NAME);
        return ADAPTER_NAME;
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

        if (!result.Success && result.ExitCode != 259)
        {
            throw new InvalidOperationException(
                $"Falha ao instalar o driver TAP (pnputil saiu com {result.ExitCode}). Saída: {result.Output.Trim()}");
        }

        logger.LogDebug("pnputil: {Output}", result.Output.Trim());
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
                $"Falha ao criar o adaptador TAP (tapctl saiu com {result.ExitCode}). Saída: {result.Output.Trim()}");
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
