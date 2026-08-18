namespace ProxyDiscord.Infrastructure.Routing;

public interface IWinDivertHandle : IDisposable
{
    bool TryReceive(byte[] buffer, out int length, out PacketAddress address, out int win32Error);

    bool Send(ReadOnlySpan<byte> packet, in PacketAddress address, out int win32Error);
}

public interface IWinDivertSocketEvents : IDisposable
{
    bool TryReceive(out SocketEvent socketEvent, out int win32Error);
}

public interface IWinDivertHandleFactory
{
    IWinDivertHandle OpenNetwork(string filter);

    IWinDivertSocketEvents OpenSocketEvents(string filter);
}

public enum SocketEventKind
{
    Bind,
    Connect,
    Listen,
    Accept,
    Close,
    Other,
}

public readonly record struct SocketEvent(
    SocketEventKind Kind,
    int ProcessId,
    byte Protocol,
    ushort LocalPort,
    ushort RemotePort,
    string? LocalIpv4,
    string? RemoteIpv4,
    bool IsIpv6);
