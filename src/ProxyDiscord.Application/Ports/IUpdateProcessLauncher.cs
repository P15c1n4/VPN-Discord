using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IUpdateProcessLauncher
{
    Task LaunchAsync(
        UpdateReleaseInfo release,
        UpdatePackageInfo package,
        int applicationProcessId,
        string installationDirectory,
        CancellationToken cancellationToken = default);
}
