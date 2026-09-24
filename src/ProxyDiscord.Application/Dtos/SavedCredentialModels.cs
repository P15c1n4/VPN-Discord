namespace ProxyDiscord.Application.Dtos;

public sealed record UserConfiguration(bool SaveCredentialsEnabled = false);

public sealed record ServerCredentials(string Username, string? Password);
