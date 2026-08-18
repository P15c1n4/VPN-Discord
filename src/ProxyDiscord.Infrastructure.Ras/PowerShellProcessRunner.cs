using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.Ras;

internal sealed class PowerShellProcessRunner(ILogger<PowerShellProcessRunner> logger)
{
    private static readonly string SCRIPT_PATH = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory,
        "Scripts", "ManageVpnConnection.ps1");

    public async Task RunAsync(IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(SCRIPT_PATH);

        foreach (var (name, value) in parameters)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            startInfo.ArgumentList.Add($"-{name}");
            startInfo.ArgumentList.Add(value);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdErr = await stdErrTask;
        var stdOut = await stdOutTask;

        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stdErr))
        {
            logger.LogWarning("Script PowerShell terminou com código {Code}. Saída: {Out} Erro: {Err}", process.ExitCode, stdOut, stdErr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Falha ao executar script de VPN (código {process.ExitCode}): {stdErr}");
            }
        }
    }
}
