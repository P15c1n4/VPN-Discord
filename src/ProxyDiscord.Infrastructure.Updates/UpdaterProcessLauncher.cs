using System.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Updates;

public sealed class UpdaterProcessLauncher : IUpdateProcessLauncher
{
    private const string UPDATER_DIRECTORY_NAME = "updater";
    private const string UPDATER_EXE_NAME = "Discord-VPN.Updater.exe";

    public Task LaunchAsync(
        UpdateReleaseInfo release,
        UpdatePackageInfo package,
        int applicationProcessId,
        string installationDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, UPDATER_DIRECTORY_NAME);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                "O instalador de atualizações não está incluído nesta instalação.");
        }

        var workerRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProxyDiscord", "UpdateWorkers");
        var workerDirectory = Path.Combine(workerRoot, Guid.NewGuid().ToString("N"));
        CopyDirectory(sourceDirectory, workerDirectory);

        var updaterPath = Path.Combine(workerDirectory, UPDATER_EXE_NAME);
        if (!File.Exists(updaterPath))
        {
            throw new FileNotFoundException("O executável separado do atualizador não foi encontrado.", updaterPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = workerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--install-dir");
        startInfo.ArgumentList.Add(Path.GetFullPath(installationDirectory));
        startInfo.ArgumentList.Add("--download-url");
        startInfo.ArgumentList.Add(package.DownloadUri.AbsoluteUri);
        startInfo.ArgumentList.Add("--expected-size");
        startInfo.ArgumentList.Add(package.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--sha256");
        startInfo.ArgumentList.Add(package.Sha256Digest ?? "");
        startInfo.ArgumentList.Add("--process-id");
        startInfo.ArgumentList.Add(applicationProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Não foi possível iniciar o instalador da versão {release.TagName}.");
        }

        return Task.CompletedTask;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destinationFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: false);
        }
    }
}
