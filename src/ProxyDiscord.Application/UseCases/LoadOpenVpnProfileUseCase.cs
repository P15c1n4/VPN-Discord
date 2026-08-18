using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Application.UseCases;

public sealed class LoadOpenVpnProfileUseCase(IOpenVpnProfileSource profileSource, ILogger<LoadOpenVpnProfileUseCase> logger)
{
    public async Task<LoadOpenVpnProfileResult> ExecuteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var profile = await profileSource.LoadAsync(filePath, cancellationToken);
            logger.LogInformation(
                "Perfil OpenVPN '{File}' carregado: {Endpoint} ({Transport})",
                profile.FileName, profile.Endpoint, profile.Transport);
            return new LoadOpenVpnProfileResult(profile, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Perfil OpenVPN '{File}' recusado", filePath);
            return new LoadOpenVpnProfileResult(null, ex.Message);
        }
    }
}

public sealed record LoadOpenVpnProfileResult(OpenVpnProfileDescriptor? Profile, string? ErrorMessage)
{
    public bool Success => Profile is not null;
}
