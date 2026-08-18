namespace ProxyDiscord.Domain.ValueObjects;

public enum TunnelProtocolScope
{
    TcpAndUdp,

    TcpOnly,

    UdpOnly,
}

public static class TunnelProtocolScopeExtensions
{
    public static bool Includes(this TunnelProtocolScope scope, TransportProtocol protocol) => protocol switch
    {
        TransportProtocol.Tcp => scope is TunnelProtocolScope.TcpAndUdp or TunnelProtocolScope.TcpOnly,
        TransportProtocol.Udp => scope is TunnelProtocolScope.TcpAndUdp or TunnelProtocolScope.UdpOnly,
        _ => false,
    };

    public static string DisplayName(this TunnelProtocolScope scope) => scope switch
    {
        TunnelProtocolScope.TcpAndUdp => "TCP e UDP",
        TunnelProtocolScope.TcpOnly => "Somente TCP",
        TunnelProtocolScope.UdpOnly => "Somente UDP",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };
}
