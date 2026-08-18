using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Infrastructure.Ras;

internal sealed class VpnAdapterLocator(ILogger<VpnAdapterLocator> logger)
{
    public NetworkInterface? FindByEntryName(string entryName)
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ppp)
            .ToList();

        var exactMatch = candidates.FirstOrDefault(ni => string.Equals(ni.Name, entryName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var upFallback = candidates.Where(ni => ni.OperationalStatus == OperationalStatus.Up).ToList();
        if (upFallback.Count == 1)
        {
            logger.LogWarning(
                "Nenhum adaptador PPP com nome exato '{Entry}' encontrado; usando o único adaptador PPP ativo '{Found}'.",
                entryName, upFallback[0].Name);
            return upFallback[0];
        }

        return null;
    }

    public bool IsUp(string entryName) => FindByEntryName(entryName)?.OperationalStatus == OperationalStatus.Up;

    public VpnAdapterInfo? ResolveAdapterInfo(string entryName)
    {
        var adapter = FindByEntryName(entryName);
        if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
        {
            return null;
        }

        var ipProperties = adapter.GetIPProperties();
        var localIp = ipProperties.UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;

        if (localIp is null)
        {
            return null;
        }

        var ipv4Properties = ipProperties.GetIPv4Properties();
        return new VpnAdapterInfo(localIp.ToString(), (uint)ipv4Properties.Index, SubInterfaceIndex: 0);
    }
}
