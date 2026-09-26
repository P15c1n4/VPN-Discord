using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace ProxyDiscord.Updater;

internal static class Program
{
    private const string APPLICATION_EXE = "Discord-VPN.exe";
    private const string WORKER_EXE = "Discord-VPN.Updater.exe";
    private const long MAXIMUM_ARCHIVE_BYTES = 1_500_000_000;
    private const long MAXIMUM_EXPANDED_BYTES = 3_000_000_000;
    private const int MAXIMUM_ARCHIVE_ENTRIES = 20_000;

    [STAThread]
    private static int Main(string[] args)
    {
        UpdateArguments? arguments = null;
        try
        {
            arguments = UpdateArguments.Parse(args);
            RunAsync(arguments).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            if (arguments is not null)
            {
                WriteFailureLog(arguments, ex);
            }

            var rollbackNote = arguments is not null
                ? TryRestartInstalledApplication(arguments)
                : "";
            MessageBox.Show(
                $"A atualização não foi concluída.\n\n{ex.Message}{rollbackNote}",
                "Atualização do Discord-VPN",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }

    private static async Task RunAsync(UpdateArguments arguments)
    {
        var installDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments.InstallDirectory));
        ValidateInstallation(installDirectory);
        await WaitForApplicationExitAsync(arguments.ApplicationProcessId);

        var parentDirectory = Directory.GetParent(installDirectory)?.FullName
                              ?? throw new InvalidOperationException("Não foi possível substituir a pasta de instalação com segurança.");
        var installName = Path.GetFileName(installDirectory);
        var updateId = Guid.NewGuid().ToString("N");
        var stageDirectory = Path.Combine(parentDirectory, $".{installName}.update-{updateId}");
        var backupDirectory = Path.Combine(parentDirectory, $".{installName}.backup-{updateId}");
        var failedDirectory = Path.Combine(parentDirectory, $".{installName}.failed-{updateId}");
        var downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProxyDiscord", "UpdateDownloads");
        Directory.CreateDirectory(downloadDirectory);
        var archivePath = Path.Combine(downloadDirectory, $"{updateId}.zip");

        var oldDirectoryMoved = false;
        try
        {
            await DownloadAndVerifyAsync(arguments, archivePath);
            Directory.CreateDirectory(stageDirectory);
            ExtractSafely(archivePath, stageDirectory);
            PreserveUserFiles(installDirectory, stageDirectory);
            ValidatePackage(stageDirectory);

            Directory.Move(installDirectory, backupDirectory);
            oldDirectoryMoved = true;
            try
            {
                Directory.Move(stageDirectory, installDirectory);
            }
            catch
            {
                Directory.Move(backupDirectory, installDirectory);
                oldDirectoryMoved = false;
                throw;
            }

            var newApplication = StartApplication(installDirectory);
            if (!await WaitForSuccessfulStartupAsync(newApplication))
            {
                throw new InvalidOperationException(
                    "A nova versão encerrou logo após iniciar; a versão anterior será restaurada.");
            }

            oldDirectoryMoved = false;
            TryDeleteDirectory(backupDirectory);
        }
        catch (Exception updateError)
        {
            if (oldDirectoryMoved)
            {
                try
                {
                    if (Directory.Exists(installDirectory))
                    {
                        Directory.Move(installDirectory, failedDirectory);
                    }

                    if (Directory.Exists(backupDirectory))
                    {
                        Directory.Move(backupDirectory, installDirectory);
                    }

                    oldDirectoryMoved = false;
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                    "A atualização falhou e não foi possível restaurar a versão anterior automaticamente.",
                        updateError,
                        rollbackError);
                }
            }

            throw;
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stageDirectory);
        }
    }

    private static void ValidateInstallation(string installDirectory)
    {
        var requiredFiles = new[]
        {
            APPLICATION_EXE,
            "WinDivert.dll",
            "WinDivert64.sys",
            Path.Combine("openvpn", "bin", "openvpn.exe"),
            Path.Combine("updater", WORKER_EXE)
        };

        if (!Directory.Exists(installDirectory))
        {
            throw new InvalidOperationException("A pasta de instalação não foi encontrada.");
        }

        if (Directory.GetParent(installDirectory) is null ||
            string.Equals(installDirectory, Path.GetPathRoot(installDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A pasta selecionada não pode ser substituída com segurança.");
        }

        var missing = requiredFiles
            .Where(relative => !File.Exists(Path.Combine(installDirectory, relative)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"A pasta selecionada não contém uma instalação completa do Discord-VPN. Arquivos ausentes: {string.Join(", ", missing)}.");
        }
    }

    private static async Task WaitForApplicationExitAsync(int processId)
    {
        Process? application = null;
        try
        {
            application = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (application)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            try
            {
                await application.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("O aplicativo não foi fechado dentro do tempo limite. Feche-o e tente atualizar novamente.");
            }
        }
    }

    private static async Task DownloadAndVerifyAsync(UpdateArguments arguments, string archivePath)
    {
        if (arguments.ExpectedSize is <= 0 or > MAXIMUM_ARCHIVE_BYTES)
        {
            throw new InvalidDataException("O tamanho do pacote publicado é inválido ou excede o limite permitido.");
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProxyDiscordUpdater", "1.0"));

        using var response = await client.GetAsync(
            arguments.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var destination = new FileStream(
                         archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         bufferSize: 128 * 1024, useAsync: true))
        {
            var buffer = new byte[128 * 1024];
            long downloaded = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                downloaded += read;
                if (downloaded > arguments.ExpectedSize || downloaded > MAXIMUM_ARCHIVE_BYTES)
                {
                    throw new InvalidDataException("O download excedeu o tamanho informado pelo GitHub.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read));
            }
        }

        var actualSize = new FileInfo(archivePath).Length;
        if (actualSize != arguments.ExpectedSize)
        {
            throw new InvalidDataException(
                $"O pacote baixado tem {actualSize} bytes, mas o GitHub informou {arguments.ExpectedSize} bytes.");
        }

        if (!string.IsNullOrWhiteSpace(arguments.ExpectedSha256))
        {
            var expectedHash = arguments.ExpectedSha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? arguments.ExpectedSha256["sha256:".Length..]
                : arguments.ExpectedSha256;
            if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("O GitHub informou um hash SHA-256 inválido para o pacote.");
            }

            await using var stream = File.OpenRead(archivePath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
            {
                throw new InvalidDataException("A verificação SHA-256 do pacote falhou. O arquivo não foi instalado.");
            }
        }
    }

    private static void ExtractSafely(string archivePath, string destinationRoot)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        long expandedBytes = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MAXIMUM_ARCHIVE_ENTRIES)
        {
            throw new InvalidDataException("O pacote de atualização está vazio ou contém arquivos demais.");
        }

        foreach (var entry in archive.Entries)
        {
            var normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedName) ||
                normalizedName.Split(Path.DirectorySeparatorChar).Any(segment => segment == ".."))
            {
                throw new InvalidDataException("O pacote contém um caminho de arquivo inválido.");
            }

            var outputPath = Path.GetFullPath(Path.Combine(fullRoot, normalizedName));
            if (!outputPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("O pacote tenta gravar arquivos fora da pasta temporária.");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            expandedBytes += entry.Length;
            if (expandedBytes > MAXIMUM_EXPANDED_BYTES)
            {
                throw new InvalidDataException("O conteúdo do pacote excede o limite de tamanho permitido.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var entryStream = entry.Open();
            using var outputStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            entryStream.CopyTo(outputStream);
        }
    }

    private static void PreserveUserFiles(string currentDirectory, string stageDirectory)
    {
        foreach (var name in new[] { "config.json", "user_auth.json", "state.json" })
        {
            var source = Path.Combine(currentDirectory, name);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(stageDirectory, name), overwrite: true);
            }
        }

        var sourceLogs = Path.Combine(currentDirectory, "logs");
        if (Directory.Exists(sourceLogs))
        {
            CopyDirectory(sourceLogs, Path.Combine(stageDirectory, "logs"));
        }
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
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void WriteFailureLog(UpdateArguments arguments, Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(Path.GetFullPath(arguments.InstallDirectory), "logs");
            Directory.CreateDirectory(logDirectory);
            var entry = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level = "Error",
                category = "ProxyDiscord.Updater",
                eventId = new { id = 1, name = "UpdateFailed" },
                message = exception.Message,
                request = new { operation = "apply-release", downloadHost = "github.com" },
                response = (object?)null,
                properties = new Dictionary<string, object?>(),
                scope = new Dictionary<string, object?>(),
                exception = new
                {
                    type = exception.GetType().FullName,
                    message = exception.Message,
                    stackTrace = exception.StackTrace,
                    fullDetails = exception.ToString(),
                },
                appState = new { stage = "updater-caught-exception" },
            };
            File.AppendAllText(
                Path.Combine(logDirectory, $"app-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"),
                JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private static void ValidatePackage(string stageDirectory)
    {
        var requiredFiles = new[]
        {
            APPLICATION_EXE,
            "WinDivert.dll",
            "WinDivert64.sys",
            Path.Combine("openvpn", "bin", "openvpn.exe"),
            Path.Combine("updater", WORKER_EXE)
        };

        var missing = requiredFiles
            .Where(relative => !File.Exists(Path.Combine(stageDirectory, relative)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"O pacote não contém uma instalação completa do Discord-VPN. Arquivos ausentes: {string.Join(", ", missing)}.");
        }
    }

    private static Process StartApplication(string installDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(installDirectory, APPLICATION_EXE),
            WorkingDirectory = installDirectory,
            UseShellExecute = false
        };

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("O Windows não conseguiu iniciar a nova versão do aplicativo.");
    }

    private static async Task<bool> WaitForSuccessfulStartupAsync(Process application)
    {
        using (application)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try
            {
                await application.WaitForExitAsync(timeout.Token);
                return false;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return !application.HasExited;
            }
        }
    }

    private static string TryRestartInstalledApplication(UpdateArguments arguments)
    {
        try
        {
            var installDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments.InstallDirectory));
            var appPath = Path.Combine(installDirectory, APPLICATION_EXE);
            if (File.Exists(appPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = appPath,
                    WorkingDirectory = installDirectory,
                    UseShellExecute = false
                });
                return "\n\nO aplicativo existente foi iniciado novamente.";
            }
        }
        catch (Exception rollbackError)
        {
            return $"\n\nA recuperação automática também falhou: {rollbackError.Message}";
        }

        return "\n\nAbra o Discord-VPN manualmente pela pasta de instalação.";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record UpdateArguments(
        string InstallDirectory,
        string DownloadUrl,
        long ExpectedSize,
        string? ExpectedSha256,
        int ApplicationProcessId)
    {
        public static UpdateArguments Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index + 1 < args.Length; index += 2)
            {
                values[args[index]] = args[index + 1];
            }

            if (!values.TryGetValue("--install-dir", out var installDirectory) ||
                !values.TryGetValue("--download-url", out var downloadUrl) ||
                !values.TryGetValue("--expected-size", out var sizeText) ||
                !values.TryGetValue("--process-id", out var processText) ||
                !long.TryParse(sizeText, out var size) ||
                !int.TryParse(processText, out var processId))
            {
                throw new ArgumentException("Os dados necessários para iniciar a atualização estão incompletos.");
            }

            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("O endereço de download da atualização não passou na validação de segurança.");
            }

            values.TryGetValue("--sha256", out var digest);
            return new UpdateArguments(installDirectory, downloadUri.AbsoluteUri, size, digest, processId);
        }
    }
}
