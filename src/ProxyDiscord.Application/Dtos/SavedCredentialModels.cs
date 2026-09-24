namespace ProxyDiscord.Application.Dtos;

public sealed record UserConfiguration(
    bool SaveCredentialsEnabled = false,
    bool SavePasswordAfterConnectionEnabled = false);

public sealed record ServerCredentials(string Username, string? Password);
