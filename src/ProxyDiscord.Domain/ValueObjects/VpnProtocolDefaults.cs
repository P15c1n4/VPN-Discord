namespace ProxyDiscord.Domain.ValueObjects;

public static class VpnProtocolDefaults
{
    public static int DefaultPort(this VpnProtocol protocol) => protocol switch
    {
        VpnProtocol.OpenVpn => 1194,
        VpnProtocol.Sstp => 443,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
    };

    public static string DisplayName(this VpnProtocol protocol) => protocol switch
    {
        VpnProtocol.OpenVpn => "OpenVPN",
        VpnProtocol.Sstp => "MS-SSTP",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
    };
}
