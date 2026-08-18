using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProxyDiscord.Infrastructure.Routing;

internal sealed class LocalAddressSet
{
    private static readonly TimeSpan REFRESH_COOLDOWN = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private HashSet<IPAddress> _addresses = Snapshot();
    private DateTime _lastRefreshUtc = DateTime.UtcNow;

    public bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        lock (_lock)
        {
            if (_addresses.Contains(address))
            {
                return true;
            }

            if (DateTime.UtcNow - _lastRefreshUtc < REFRESH_COOLDOWN)
            {
                return false;
            }

            _addresses = Snapshot();
            _lastRefreshUtc = DateTime.UtcNow;
            return _addresses.Contains(address);
        }
    }

    private static HashSet<IPAddress> Snapshot()
    {
        var addresses = new HashSet<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }

        return addresses;
    }
}
