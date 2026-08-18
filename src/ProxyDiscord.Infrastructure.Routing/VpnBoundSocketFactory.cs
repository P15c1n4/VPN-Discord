using System.Net;
using System.Net.Sockets;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Infrastructure.Routing;

public static class VpnBoundSocketFactory
{
    private const SocketOptionName IP_UNICAST_IF = (SocketOptionName)31;

    public static Socket CreateTcpSocket(VpnAdapterInfo adapter)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        PinToVpn(socket, adapter);
        return socket;
    }

    public static Socket CreateUdpSocket(VpnAdapterInfo adapter)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        PinToVpn(socket, adapter);
        return socket;
    }

    private static void PinToVpn(Socket socket, VpnAdapterInfo adapter)
    {
        var networkOrderIndex = IPAddress.HostToNetworkOrder((int)adapter.InterfaceIndex);
        socket.SetSocketOption(SocketOptionLevel.IP, IP_UNICAST_IF, networkOrderIndex);

        if (IPAddress.TryParse(adapter.LocalIp, out var localIp) &&
            localIp.AddressFamily == AddressFamily.InterNetwork)
        {
            socket.Bind(new IPEndPoint(localIp, 0));
        }
    }
}
