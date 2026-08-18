using System.Runtime.InteropServices;

namespace ProxyDiscord.Infrastructure.WinDivert;

internal static class WinDivertNative
{
    private const string DLL = "WinDivert.dll";

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        WinDivertLayer layer,
        short priority,
        ulong flags);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertRecv(
        IntPtr handle,
        IntPtr pPacket,
        uint packetLen,
        out uint pRecvLen,
        ref WinDivertAddress pAddr);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSend(
        IntPtr handle,
        IntPtr pPacket,
        uint packetLen,
        out uint pSendLen,
        ref WinDivertAddress pAddr);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertShutdown(IntPtr handle, WinDivertShutdownHow how);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSetParam(IntPtr handle, WinDivertParam param, ulong value);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperCalcChecksums(
        IntPtr pPacket,
        uint packetLen,
        ref WinDivertAddress pAddr,
        ulong flags);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperFormatIPv6Address(
        ref uint pAddr,
        [Out][MarshalAs(UnmanagedType.LPArray)] byte[] buffer,
        uint bufLen);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperCompileFilter(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        WinDivertLayer layer,
        IntPtr @object,
        uint objLen,
        out IntPtr errorStr,
        out uint errorPos);
}

internal enum WinDivertLayer
{
    Network = 0,
    NetworkForward = 1,
    Flow = 2,
    Socket = 3,
    Reflect = 4,
}

internal enum WinDivertParam
{
    QueueLength = 0,
    QueueTime = 1,
    QueueSize = 2,
}

internal enum WinDivertShutdownHow
{
    Recv = 1,
    Send = 2,
    Both = 3,
}

[Flags]
internal enum WinDivertOpenFlags : ulong
{
    None = 0,
    Sniff = 0x0001,
    Drop = 0x0002,
    RecvOnly = 0x0004,
    SendOnly = 0x0008,
    NoInstall = 0x0010,
    Fragments = 0x0020,
}

internal enum WinDivertEvent : byte
{
    NetworkPacket = 0,
    FlowEstablished = 1,
    FlowDeleted = 2,
    SocketBind = 3,
    SocketConnect = 4,
    SocketListen = 5,
    SocketAccept = 6,
    SocketClose = 7,
    ReflectOpen = 8,
    ReflectClose = 9,
}

[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct WinDivertAddress
{
    [FieldOffset(0)] public long Timestamp;

    [FieldOffset(8)] public uint Bits;

    [FieldOffset(12)] public uint Reserved2;

    [FieldOffset(16)] public uint IfIdx;
    [FieldOffset(20)] public uint SubIfIdx;

    [FieldOffset(32)] public uint ProcessId;

    [FieldOffset(36)] public uint LocalAddr0;
    [FieldOffset(40)] public uint LocalAddr1;
    [FieldOffset(44)] public uint LocalAddr2;
    [FieldOffset(48)] public uint LocalAddr3;

    [FieldOffset(52)] public uint RemoteAddr0;
    [FieldOffset(56)] public uint RemoteAddr1;
    [FieldOffset(60)] public uint RemoteAddr2;
    [FieldOffset(64)] public uint RemoteAddr3;

    [FieldOffset(68)] public ushort LocalPort;
    [FieldOffset(70)] public ushort RemotePort;
    [FieldOffset(72)] public byte Protocol;

    private const int SNIFFED_BIT = 16;
    private const int OUTBOUND_BIT = 17;
    private const int LOOPBACK_BIT = 18;
    private const int IMPOSTOR_BIT = 19;
    private const int IPV6_BIT = 20;

    public readonly WinDivertLayer Layer => (WinDivertLayer)(Bits & 0xFF);

    public readonly WinDivertEvent Event => (WinDivertEvent)((Bits >> 8) & 0xFF);

    public bool Outbound
    {
        readonly get => GetBit(OUTBOUND_BIT);
        set => SetBit(OUTBOUND_BIT, value);
    }

    public readonly bool Sniffed => GetBit(SNIFFED_BIT);

    public readonly bool Loopback => GetBit(LOOPBACK_BIT);

    public readonly bool Impostor => GetBit(IMPOSTOR_BIT);

    public readonly bool IsIpv6 => GetBit(IPV6_BIT);

    private readonly bool GetBit(int bit) => (Bits & (1u << bit)) != 0;

    private void SetBit(int bit, bool value)
    {
        if (value)
        {
            Bits |= 1u << bit;
        }
        else
        {
            Bits &= ~(1u << bit);
        }
    }

    public string FormatLocalAddr() => FormatAddress(ref LocalAddr0);

    public string FormatRemoteAddr() => FormatAddress(ref RemoteAddr0);

    private static string FormatAddress(ref uint firstWord)
    {
        const int BUFFER_LENGTH = 64;
        var buffer = new byte[BUFFER_LENGTH];
        if (!WinDivertNative.WinDivertHelperFormatIPv6Address(ref firstWord, buffer, BUFFER_LENGTH))
        {
            return string.Empty;
        }

        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, end < 0 ? BUFFER_LENGTH : end);
    }
}
