using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IServerCredentialsStore
{
    Task<ServerCredentials?> FindAsync(string serverKey, CancellationToken cancellationToken = default);

    // A null password means no new manual password was entered; the adapter preserves the
    // existing password only while the username remains unchanged.
    Task SaveAsync(
        string serverKey,
        string username,
        string? manuallyEnteredPassword,
        CancellationToken cancellationToken = default);
}
