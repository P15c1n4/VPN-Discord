using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Application.UseCases;

public sealed class SavedCredentialsUseCase(
    IUserConfigurationStore configurationStore,
    IServerCredentialsStore credentialsStore)
{
    public Task<UserConfiguration> ReadConfigurationAsync(CancellationToken cancellationToken = default) =>
        configurationStore.ReadAsync(cancellationToken);

    public Task WriteConfigurationAsync(
        UserConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        configurationStore.WriteAsync(configuration, cancellationToken);

    public async Task<ServerCredentials?> FindAsync(
        string serverKey,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurationStore.ReadAsync(cancellationToken);
        return configuration.SaveCredentialsEnabled
            ? await credentialsStore.FindAsync(serverKey, cancellationToken)
            : null;
    }

    public async Task SaveAfterSuccessfulConnectionAsync(
        string serverKey,
        string username,
        string? manuallyEnteredPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(manuallyEnteredPassword))
        {
            return;
        }

        var configuration = await configurationStore.ReadAsync(cancellationToken);
        if (!configuration.SaveCredentialsEnabled)
        {
            return;
        }

        await credentialsStore.SaveAsync(
            serverKey,
            username,
            manuallyEnteredPassword,
            cancellationToken);
    }
}
