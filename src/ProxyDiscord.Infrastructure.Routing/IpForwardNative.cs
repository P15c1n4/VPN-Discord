using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ProxyDiscord.Infrastructure.Routing;

internal static class IpForwardNative
{
    private const string DLL = "iphlpapi.dll";

    public const int EXPECTED_ROW_SIZE = 104;
    public const int EXPECTED_SOCKADDR_SIZE = 28;
    public const ushort AF_INET = 2;

    public const uint NO_ERROR = 0;
    public const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;
    public const uint ERROR_NOT_FOUND = 1168;

    public const uint MIB_IPPROTO_NET_MGMT = 3;

    [DllImport(DLL)]
    public static extern void InitializeIpForwardEntry(ref MibIpForwardRow2 row);

    [DllImport(DLL)]
    public static extern uint CreateIpForwardEntry2(ref MibIpForwardRow2 row);

    [DllImport(DLL)]
    public static extern uint DeleteIpForwardEntry2(ref MibIpForwardRow2 row);

    [DllImport(DLL)]
    public static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport(DLL)]
    public static extern void FreeMibTable(IntPtr table);

    public static IReadOnlyList<MibIpForwardRow2> ReadIpv4Table()
    {
        var status = GetIpForwardTable2(AF_INET, out var table);
        if (status != NO_ERROR || table == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var first = IntPtr.Add(table, 8);
            var rows = new List<MibIpForwardRow2>(count);
            for (var i = 0; i < count; i++)
            {
                rows.Add(Marshal.PtrToStructure<MibIpForwardRow2>(IntPtr.Add(first, i * EXPECTED_ROW_SIZE)));
            }

            return rows;
        }
        finally
        {
            FreeMibTable(table);
        }
    }
}

[StructLayout(LayoutKind.Sequential, Size = IpForwardNative.EXPECTED_SOCKADDR_SIZE)]
internal struct SockaddrInet
{
    public ushort Family;
    public ushort Port;

    public uint Ipv4;

    public uint Addr0;
    public uint Addr1;
    public uint Addr2;
    public uint Addr3;
    public uint ScopeId;

    public static SockaddrInet FromIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Somente endereços IPv4 são suportados aqui.", nameof(address));
        }

        return new SockaddrInet
        {
            Family = IpForwardNative.AF_INET,
            Ipv4 = BitConverter.ToUInt32(address.GetAddressBytes()),
        };
    }

    public readonly IPAddress ToIpv4() => new(BitConverter.GetBytes(Ipv4));
}

[StructLayout(LayoutKind.Sequential)]
internal struct IpAddressPrefix
{
    public SockaddrInet Prefix;
    public byte PrefixLength;
}

[StructLayout(LayoutKind.Sequential, Size = IpForwardNative.EXPECTED_ROW_SIZE)]
internal struct MibIpForwardRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public IpAddressPrefix DestinationPrefix;
    public SockaddrInet NextHop;
    public byte SitePrefixLength;
    public uint ValidLifetime;
    public uint PreferredLifetime;
    public uint Metric;
    public uint Protocol;
    public byte Loopback;
    public byte AutoconfigureAddress;
    public byte Publish;
    public byte Immortal;
    public uint Age;
    public uint Origin;

    public readonly bool IsDefaultRoute =>
        DestinationPrefix.PrefixLength == 0 && DestinationPrefix.Prefix.Ipv4 == 0;

    public readonly string Describe() =>
        $"if={InterfaceIndex} {DestinationPrefix.Prefix.ToIpv4()}/{DestinationPrefix.PrefixLength} " +
        $"via {NextHop.ToIpv4()} metric={Metric}";
}
