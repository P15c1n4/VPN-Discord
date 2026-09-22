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

    // Fixa o socket numa interface física (ex.: a da rota padrão), sem depender de qual rota
    // padrão o Windows escolheria — com a rota do túnel instalada, essa escolha não é confiável.
    public static Socket CreateTcpSocketOnInterface(uint interfaceIndex)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        PinToInterface(socket, interfaceIndex);
        return socket;
    }

    private static void PinToInterface(Socket socket, uint interfaceIndex)
    {
        var networkOrderIndex = IPAddress.HostToNetworkOrder((int)interfaceIndex);
        socket.SetSocketOption(SocketOptionLevel.IP, IP_UNICAST_IF, networkOrderIndex);
    }

    private static void PinToVpn(Socket socket, VpnAdapterInfo adapter)
    {
        PinToInterface(socket, adapter.InterfaceIndex);

        if (IPAddress.TryParse(adapter.LocalIp, out var localIp) &&
            localIp.AddressFamily == AddressFamily.InterNetwork)
        {
            socket.Bind(new IPEndPoint(localIp, 0));
        }
    }
}
