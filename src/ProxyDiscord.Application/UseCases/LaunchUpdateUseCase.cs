using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Application.UseCases;

public sealed class LaunchUpdateUseCase(IUpdateProcessLauncher processLauncher)
{
    public Task ExecuteAsync(
        Version currentVersion,
        Dtos.UpdateReleaseInfo release,
        int applicationProcessId,
        string installationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(release);

        if (release.Version <= currentVersion)
        {
            throw new InvalidOperationException("A versão selecionada não é mais recente que a instalada.");
        }

        if (release.Package is not { } package)
        {
            throw new InvalidOperationException(
                "A versão foi publicada sem o pacote Discord-VPN-win-x64.zip necessário para a atualização.");
        }

        if (applicationProcessId <= 0 || string.IsNullOrWhiteSpace(installationDirectory))
        {
            throw new InvalidOperationException("Não foi possível localizar a instalação atual do aplicativo.");
        }

        return processLauncher.LaunchAsync(
            release, package, applicationProcessId, installationDirectory, cancellationToken);
    }
}
