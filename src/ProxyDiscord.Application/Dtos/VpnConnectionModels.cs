using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Dtos;

public sealed record VpnConnectionRequest(
    HostEndpoint Endpoint,
    VpnProtocol Protocol,
    string? Username,
    string? Password,
    string EntryNameHint,
    string? OpenVpnConfigBase64 = null,
    bool UseProfileOpenVpnCredentials = false);

public sealed record VpnConnectionResult(bool Success, VpnLinkStatus Status, string? ErrorMessage)
{
    public static VpnConnectionResult Ok(VpnLinkStatus status) => new(true, status, null);
    public static VpnConnectionResult Failed(VpnLinkStatus status, string error) => new(false, status, error);
}

public enum VpnLinkStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed record VpnAdapterInfo(
    string LocalIp,
    uint InterfaceIndex,
    uint SubInterfaceIndex,
    string? GatewayIp = null);

public sealed record OutboundInterfaceInfo(
    string LocalIp,
    uint InterfaceIndex,
    string Alias);

public sealed class VpnConnectionLostEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}
