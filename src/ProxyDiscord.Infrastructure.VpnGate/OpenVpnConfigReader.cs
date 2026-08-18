using System.Text;
using ProxyDiscord.Application.Vpn;

namespace ProxyDiscord.Infrastructure.VpnGate;

internal static class OpenVpnConfigReader
{
    public static OpenVpnRemote? TryRead(string? openVpnConfigBase64) =>
        OpenVpnRemoteParser.TryParse(TryDecode(openVpnConfigBase64));

    private static string? TryDecode(string? openVpnConfigBase64)
    {
        if (string.IsNullOrWhiteSpace(openVpnConfigBase64))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(openVpnConfigBase64.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
