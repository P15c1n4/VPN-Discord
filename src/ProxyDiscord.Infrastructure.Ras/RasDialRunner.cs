using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Infrastructure.Ras;

internal sealed class RasDialRunner(ILogger<RasDialRunner> logger)
{
    public async Task<bool> DialAsync(string entryName, string username, string password, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunAsync([entryName, username, password], cancellationToken);
        if (exitCode != 0)
        {
            logger.LogWarning("rasdial retornou código {Code} ao discar '{Entry}': {Output}", exitCode, entryName, output);
        }

        return exitCode == 0;
    }

    public async Task HangUpAsync(string entryName, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunAsync([entryName, "/DISCONNECT"], cancellationToken);
        if (exitCode != 0)
        {
            logger.LogDebug("rasdial /DISCONNECT retornou código {Code} para '{Entry}' (pode já estar desconectada): {Output}", exitCode, entryName, output);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "rasdial.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;

        return (process.ExitCode, output);
    }
}
