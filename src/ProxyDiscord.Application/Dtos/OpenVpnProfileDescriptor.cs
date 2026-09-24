using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Dtos;

public sealed record OpenVpnProfileDescriptor(
    string FileName,
    string FilePath,
    string ConfigBase64,
    HostEndpoint Endpoint,
    TransportProtocol Transport,
    OpenVpnAuthenticationInfo Authentication);

public sealed record OpenVpnAuthenticationInfo(
    OpenVpnAuthenticationKind Kind,
    bool ProfileOptionAvailable);

public enum OpenVpnAuthenticationKind
{
    InlineCredentials,
    ExternalCredentialsFile,
    InteractivePrompt,
    NoUsernamePasswordDirective
}

public enum OpenVpnCredentialSource
{
    Profile,
    Local
}
