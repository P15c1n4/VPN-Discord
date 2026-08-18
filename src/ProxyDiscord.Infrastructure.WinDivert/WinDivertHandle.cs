using System.ComponentModel;
using System.Runtime.InteropServices;
using ProxyDiscord.Infrastructure.Routing;

namespace ProxyDiscord.Infrastructure.WinDivert;

internal sealed class WinDivertHandle : IWinDivertHandle
{
    private const ulong QUEUE_LENGTH = 16384;
    private const ulong QUEUE_SIZE = 33554432;
    private const ulong QUEUE_TIME = 4000;

    private readonly IntPtr _handle;
    private readonly IntPtr _packetBuffer;
    private readonly byte[] _sendStaging;
    private readonly int _packetBufferSize;
    private readonly object _sendLock = new();
    private bool _disposed;

    public WinDivertHandle(string filter, int bufferSize = ushort.MaxValue)
    {
        _handle = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Network, 0, (ulong)WinDivertOpenFlags.None);
        if (_handle == WinDivertNative.INVALID_HANDLE_VALUE)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, WinDivertOpenFailure.Describe(error, filter, "NETWORK"));
        }

        WinDivertNative.WinDivertSetParam(_handle, WinDivertParam.QueueLength, QUEUE_LENGTH);
        WinDivertNative.WinDivertSetParam(_handle, WinDivertParam.QueueSize, QUEUE_SIZE);
        WinDivertNative.WinDivertSetParam(_handle, WinDivertParam.QueueTime, QUEUE_TIME);

        _packetBufferSize = bufferSize;
        _packetBuffer = Marshal.AllocHGlobal(bufferSize);
        _sendStaging = new byte[bufferSize];
    }

    public bool TryReceive(byte[] buffer, out int length, out PacketAddress address, out int win32Error)
    {
        var native = default(WinDivertAddress);
        var received = WinDivertNative.WinDivertRecv(
            _handle, _packetBuffer, (uint)_packetBufferSize, out var readLen, ref native);

        if (!received)
        {
            win32Error = Marshal.GetLastWin32Error();
            length = 0;
            address = default;
            return false;
        }

        win32Error = 0;
        length = (int)Math.Min(readLen, (uint)buffer.Length);
        Marshal.Copy(_packetBuffer, buffer, 0, length);
        address = ToPacketAddress(native);
        return true;
    }

    public bool Send(ReadOnlySpan<byte> packet, in PacketAddress address, out int win32Error)
    {
        var native = FromPacketAddress(address);

        lock (_sendLock)
        {
            if (packet.Length > _packetBufferSize)
            {
                win32Error = 0;
                return false;
            }

            packet.CopyTo(_sendStaging);
            Marshal.Copy(_sendStaging, 0, _packetBuffer, packet.Length);

            WinDivertNative.WinDivertHelperCalcChecksums(_packetBuffer, (uint)packet.Length, ref native, 0);

            var sent = WinDivertNative.WinDivertSend(
                _handle, _packetBuffer, (uint)packet.Length, out _, ref native);
            win32Error = sent ? 0 : Marshal.GetLastWin32Error();
            return sent;
        }
    }

    private static PacketAddress ToPacketAddress(in WinDivertAddress native)
    {
        var raw = new byte[PacketAddress.RAW_SIZE];
        MemoryMarshal.Write(raw, in native);
        return new PacketAddress(raw, native.Outbound, native.IfIdx, native.Loopback, native.Impostor, native.IsIpv6);
    }

    private static WinDivertAddress FromPacketAddress(in PacketAddress address)
    {
        var native = MemoryMarshal.Read<WinDivertAddress>(address.Raw);
        native.Outbound = address.Outbound;
        return native;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WinDivertNative.WinDivertShutdown(_handle, WinDivertShutdownHow.Both);
        WinDivertNative.WinDivertClose(_handle);
        Marshal.FreeHGlobal(_packetBuffer);
    }
}
