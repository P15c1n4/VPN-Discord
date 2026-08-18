using System.Runtime.InteropServices;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class IpHelperTableReader : IIpHelperTableReader
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    public IReadOnlyDictionary<(TransportProtocol Protocol, int LocalPort), int> SnapshotOwnerPids()
    {
        var result = new Dictionary<(TransportProtocol, int), int>();

        foreach (var (protocol, localPort, pid) in ReadTcpTable())
        {
            result[(protocol, localPort)] = pid;
        }

        foreach (var (protocol, localPort, pid) in ReadUdpTable())
        {
            result[(protocol, localPort)] = pid;
        }

        return result;
    }

    private delegate int TableQuery(IntPtr buffer, ref int size);

    private const int TCP_ROW_SIZE = 24;
    private const int UDP_ROW_SIZE = 12;

    private static IEnumerable<(TransportProtocol Protocol, int LocalPort, int Pid)> ReadTcpTable()
    {
        var buffer = QueryTable((IntPtr ptr, ref int size) => GetExtendedTcpTable(ptr, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0));
        if (buffer is null)
        {
            yield break;
        }

        var numEntries = BitConverter.ToInt32(buffer, 0);
        for (var i = 0; i < numEntries; i++)
        {
            var rowOffset = sizeof(int) + i * TCP_ROW_SIZE;
            var localPort = BitConverter.ToInt32(buffer, rowOffset + 8);
            var owningPid = BitConverter.ToInt32(buffer, rowOffset + 20);
            yield return (TransportProtocol.Tcp, SwapPort(localPort), owningPid);
        }
    }

    private static IEnumerable<(TransportProtocol Protocol, int LocalPort, int Pid)> ReadUdpTable()
    {
        var buffer = QueryTable((IntPtr ptr, ref int size) => GetExtendedUdpTable(ptr, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0));
        if (buffer is null)
        {
            yield break;
        }

        var numEntries = BitConverter.ToInt32(buffer, 0);
        for (var i = 0; i < numEntries; i++)
        {
            var rowOffset = sizeof(int) + i * UDP_ROW_SIZE;
            var localPort = BitConverter.ToInt32(buffer, rowOffset + 4);
            var owningPid = BitConverter.ToInt32(buffer, rowOffset + 8);
            yield return (TransportProtocol.Udp, SwapPort(localPort), owningPid);
        }
    }

    private static byte[]? QueryTable(TableQuery query)
    {
        var size = 0;
        var result = query(IntPtr.Zero, ref size);
        if (result != ERROR_INSUFFICIENT_BUFFER || size <= 0)
        {
            return null;
        }

        var buffer = new byte[size];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            result = query(handle.AddrOfPinnedObject(), ref size);
            return result == 0 ? buffer : null;
        }
        finally
        {
            handle.Free();
        }
    }

    private static int SwapPort(int rawPort) => ((rawPort & 0xFF) << 8) | ((rawPort & 0xFF00) >> 8);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool sort, int ipVersion, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedUdpTable(IntPtr udpTable, ref int size, bool sort, int ipVersion, int tableClass, int reserved);
}
