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
        PinToInterface(socket, adapter.InterfaceIndex, adapter.LocalIp);
        return socket;
    }

    public static Socket CreateTcpSocket(OutboundInterfaceInfo networkInterface)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        PinToInterface(socket, networkInterface.InterfaceIndex, networkInterface.LocalIp);
        return socket;
    }

    public static Socket CreateUdpSocket(VpnAdapterInfo adapter)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        PinToInterface(socket, adapter.InterfaceIndex, adapter.LocalIp);
        return socket;
    }

    private static void PinToInterface(Socket socket, uint interfaceIndex, string localIpText)
    {
        var networkOrderIndex = IPAddress.HostToNetworkOrder((int)interfaceIndex);
        socket.SetSocketOption(SocketOptionLevel.IP, IP_UNICAST_IF, networkOrderIndex);

        if (IPAddress.TryParse(localIpText, out var localIp) &&
            localIp.AddressFamily == AddressFamily.InterNetwork)
        {
            socket.Bind(new IPEndPoint(localIp, 0));
        }
    }
}
