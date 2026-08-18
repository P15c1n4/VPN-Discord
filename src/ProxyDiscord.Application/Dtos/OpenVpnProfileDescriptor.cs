using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Dtos;

public sealed record OpenVpnProfileDescriptor(
    string FileName,
    string FilePath,
    string ConfigBase64,
    HostEndpoint Endpoint,
    TransportProtocol Transport);
