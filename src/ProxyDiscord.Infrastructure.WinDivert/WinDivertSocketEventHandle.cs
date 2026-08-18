using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ProxyDiscord.Infrastructure.Routing;

namespace ProxyDiscord.Infrastructure.WinDivert;

internal sealed class WinDivertSocketEventHandle : IWinDivertSocketEvents
{
    private readonly IntPtr _handle;
    private bool _disposed;

    public WinDivertSocketEventHandle(string filter)
    {
        const ulong FLAGS = (ulong)(WinDivertOpenFlags.Sniff | WinDivertOpenFlags.RecvOnly);
        _handle = WinDivertNative.WinDivertOpen(filter, WinDivertLayer.Socket, 0, FLAGS);
        if (_handle == WinDivertNative.INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"WinDivertOpen (camada SOCKET) falhou para o filtro '{filter}'.");
        }
    }

    public bool TryReceive(out SocketEvent socketEvent, out int win32Error)
    {
        var native = default(WinDivertAddress);

        var received = WinDivertNative.WinDivertRecv(_handle, IntPtr.Zero, 0, out _, ref native);
        if (!received)
        {
            win32Error = Marshal.GetLastWin32Error();
            socketEvent = default;
            return false;
        }

        win32Error = 0;
        socketEvent = new SocketEvent(
            MapKind(native.Event),
            (int)native.ProcessId,
            native.Protocol,
            native.LocalPort,
            native.RemotePort,
            ToIpv4(native.FormatLocalAddr()),
            ToIpv4(native.FormatRemoteAddr()),
            native.IsIpv6);
        return true;
    }

    private static string? ToIpv4(string formatted)
    {
        if (string.IsNullOrEmpty(formatted) || !IPAddress.TryParse(formatted, out var parsed))
        {
            return null;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            return parsed.Equals(IPAddress.Any) ? null : parsed.ToString();
        }

        if (!parsed.IsIPv4MappedToIPv6)
        {
            return null;
        }

        var mapped = parsed.MapToIPv4();
        return mapped.Equals(IPAddress.Any) ? null : mapped.ToString();
    }

    private static SocketEventKind MapKind(WinDivertEvent nativeEvent) => nativeEvent switch
    {
        WinDivertEvent.SocketBind => SocketEventKind.Bind,
        WinDivertEvent.SocketConnect => SocketEventKind.Connect,
        WinDivertEvent.SocketListen => SocketEventKind.Listen,
        WinDivertEvent.SocketAccept => SocketEventKind.Accept,
        WinDivertEvent.SocketClose => SocketEventKind.Close,
        _ => SocketEventKind.Other,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WinDivertNative.WinDivertShutdown(_handle, WinDivertShutdownHow.Both);
        WinDivertNative.WinDivertClose(_handle);
    }
}
