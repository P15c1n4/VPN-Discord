namespace ProxyDiscord.Application.Dtos;

public sealed record UserConfiguration(
    bool SaveCredentialsEnabled = false,
    bool SavePasswordAfterConnectionEnabled = false,
    ProcessRoutingBackend RoutingBackend = ProcessRoutingBackend.WinDivert);

public enum ProcessRoutingBackend
{
    WinDivert,
    ProxiFyre,
}

public sealed record ServerCredentials(string Username, string? Password);
