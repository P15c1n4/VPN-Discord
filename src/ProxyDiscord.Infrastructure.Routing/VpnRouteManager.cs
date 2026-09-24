using System.Net;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class VpnRouteManager(
    IVpnInterfaceGatewayResolver gatewayResolver,
    ILogger<VpnRouteManager> logger) : IVpnRouteManager
{
    public const uint TUNNEL_ROUTE_METRIC = 9000;

    private readonly object _lock = new();
    private MibIpForwardRow2? _installedRoute;

    public bool HasRoute
    {
        get
        {
            lock (_lock)
            {
                return _installedRoute is not null;
            }
        }
    }

    public void EnsureTunnelDefaultRoute(VpnAdapterInfo adapter)
    {
        lock (_lock)
        {
            if (_installedRoute is not null)
            {
                RemoveCore();
            }

            LogInterfaceRoutes(adapter.InterfaceIndex, "antes");

            var nextHop = ResolveNextHop(adapter);
            var row = BuildDefaultRoute(adapter.InterfaceIndex, nextHop);

            var status = IpForwardNative.CreateIpForwardEntry2(ref row);
            if (status == IpForwardNative.ERROR_OBJECT_ALREADY_EXISTS)
            {
                logger.LogInformation(
                    "Rota padrão já existente na interface VPN {IfIdx}; reaproveitando.", adapter.InterfaceIndex);
                _installedRoute = row;
                LogInterfaceRoutes(adapter.InterfaceIndex, "depois");
                return;
            }

            if (status != IpForwardNative.NO_ERROR)
            {
                throw new InvalidOperationException(
                    $"Não foi possível adicionar a rota IPv4 padrão à interface VPN {adapter.InterfaceIndex} " +
                    $"(gateway {nextHop}; código Win32 {status}). Sem essa rota, as conexões pela VPN " +
                    "não terão acesso à internet.");
            }

            _installedRoute = row;
            logger.LogInformation(
                "Rota do túnel instalada: 0.0.0.0/0 via {NextHop} na interface {IfIdx}, métrica {Metric}.",
                nextHop, adapter.InterfaceIndex, TUNNEL_ROUTE_METRIC);

            LogInterfaceRoutes(adapter.InterfaceIndex, "depois");
        }
    }

    public void RemoveTunnelDefaultRoute()
    {
        lock (_lock)
        {
            RemoveCore();
        }
    }

    public int RemoveOrphanedRoutes()
    {
        var removed = 0;
        foreach (var row in IpForwardNative.ReadIpv4Table())
        {
            if (!row.IsDefaultRoute || row.Metric != TUNNEL_ROUTE_METRIC)
            {
                continue;
            }

            var candidate = row;
            if (IpForwardNative.DeleteIpForwardEntry2(ref candidate) == IpForwardNative.NO_ERROR)
            {
                removed++;
                logger.LogInformation("Rota órfã do túnel removida: {Route}", candidate.Describe());
            }
        }

        return removed;
    }

    private void RemoveCore()
    {
        if (_installedRoute is not { } route)
        {
            return;
        }

        _installedRoute = null;

        var status = IpForwardNative.DeleteIpForwardEntry2(ref route);
        if (status is IpForwardNative.NO_ERROR or IpForwardNative.ERROR_NOT_FOUND)
        {
            logger.LogInformation("Rota do túnel removida da interface {IfIdx}.", route.InterfaceIndex);
            return;
        }

        logger.LogWarning(
            "DeleteIpForwardEntry2 retornou {Status} ao remover a rota do túnel da interface {IfIdx}.",
            status, route.InterfaceIndex);
    }

    private static MibIpForwardRow2 BuildDefaultRoute(uint interfaceIndex, IPAddress nextHop)
    {
        var row = default(MibIpForwardRow2);
        IpForwardNative.InitializeIpForwardEntry(ref row);

        row.InterfaceIndex = interfaceIndex;
        row.DestinationPrefix = new IpAddressPrefix
        {
            Prefix = SockaddrInet.FromIpv4(IPAddress.Any),
            PrefixLength = 0,
        };
        row.NextHop = SockaddrInet.FromIpv4(nextHop);
        row.Metric = TUNNEL_ROUTE_METRIC;
        row.Protocol = IpForwardNative.MIB_IPPROTO_NET_MGMT;
        return row;
    }

    private IPAddress ResolveNextHop(VpnAdapterInfo adapter)
    {
        if (!string.IsNullOrWhiteSpace(adapter.GatewayIp) &&
            IPAddress.TryParse(adapter.GatewayIp, out var announcedGateway) &&
            IsUsableGateway(announcedGateway))
        {
            logger.LogDebug("Usando gateway {Gateway} anunciado pela VPN como next hop.", announcedGateway);
            return announcedGateway;
        }

        var routeGateway = gatewayResolver.ResolveGateway(adapter.InterfaceIndex);
        if (routeGateway is not null && IsUsableGateway(routeGateway))
        {
            logger.LogInformation(
                "A interface VPN {IfIdx} não anunciou gateway; usando o peer {Gateway} encontrado na tabela de rotas.",
                adapter.InterfaceIndex,
                routeGateway);
            return routeGateway;
        }

        throw new InvalidOperationException(
            $"A interface VPN {adapter.InterfaceIndex} não informou um gateway IPv4 utilizável, e nenhum peer " +
            "foi encontrado nas rotas existentes. A rota padrão não foi instalada.");
    }

    private static bool IsUsableGateway(IPAddress gateway) =>
        gateway.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        !gateway.Equals(IPAddress.Any) &&
        !gateway.Equals(IPAddress.Broadcast) &&
        !IPAddress.IsLoopback(gateway);

    private void LogInterfaceRoutes(uint interfaceIndex, string moment)
    {
        var rows = IpForwardNative.ReadIpv4Table()
            .Where(row => row.InterfaceIndex == interfaceIndex)
            .Select(row => row.Describe())
            .ToList();

        logger.LogInformation(
            "Rotas da interface VPN {IfIdx} ({Moment}): {Routes}",
            interfaceIndex, moment, rows.Count == 0 ? "(nenhuma)" : string.Join(" | ", rows));
    }
}
