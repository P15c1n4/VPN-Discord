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
        if (!configuration.SaveCredentialsEnabled)
        {
            return null;
        }

        var credentials = await credentialsStore.FindAsync(serverKey, cancellationToken);
        return credentials is null || configuration.SavePasswordAfterConnectionEnabled
            ? credentials
            : credentials with { Password = null };
    }

    public async Task SaveAfterSuccessfulConnectionAsync(
        string serverKey,
        string username,
        string? manuallyEnteredPassword,
        bool connectionSucceeded,
        CancellationToken cancellationToken = default)
    {
        if (!connectionSucceeded)
        {
            return;
        }

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
            configuration.SavePasswordAfterConnectionEnabled ? manuallyEnteredPassword : null,
            cancellationToken);
    }
}
