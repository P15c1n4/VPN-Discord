using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Application.Ports;

public interface IOpenVpnProfileSource
{
    Task<OpenVpnProfileDescriptor> LoadAsync(string filePath, CancellationToken cancellationToken = default);
}
