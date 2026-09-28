namespace ProxyDiscord.Application.Ports;

public enum OpenVpnPromptKind
{
    PrivateKeyPassphrase,
    StaticChallenge,
}

public sealed record OpenVpnPromptRequest(
    OpenVpnPromptKind Kind,
    string Message,
    string? Username,
    string? Password,
    bool ChallengeResponseEcho);

public sealed record OpenVpnPromptResponse(
    string? Username,
    string? Password,
    string? ChallengeResponse);

public interface IOpenVpnInteractivePrompt
{
    Task<OpenVpnPromptResponse?> RequestAsync(
        OpenVpnPromptRequest request,
        CancellationToken cancellationToken);
}
