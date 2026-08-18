using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.UseCases;

public sealed record ConnectVpnCommand(
    ProcessInfo TargetProcess,
    string ServerAddressRaw,
    VpnProtocol Protocol,
    string Username,
    string Password,
    string? OpenVpnConfigBase64 = null,
    Dtos.TunnelDnsSettings? Dns = null,
    TunnelProtocolScope ProtocolScope = TunnelProtocolScope.TcpAndUdp)
{
    public Dtos.TunnelDnsSettings DnsSettings => Dns ?? Dtos.TunnelDnsSettings.Default;
}
