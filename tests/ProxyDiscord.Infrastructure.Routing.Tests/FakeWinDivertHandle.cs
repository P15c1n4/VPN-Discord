using System.Collections.Concurrent;

namespace ProxyDiscord.Infrastructure.Routing.Tests;

internal sealed class FakeWinDivertHandle : IWinDivertHandle
{
    private readonly BlockingCollection<(byte[] Packet, PacketAddress Address)> _incoming = new();

    public ConcurrentQueue<(byte[] Packet, PacketAddress Address)> SentPackets { get; } = new();

    public int SendFailureWin32Error { get; set; }

    public void Enqueue(byte[] packet, PacketAddress address) => _incoming.Add((packet, address));

    public bool TryReceive(byte[] buffer, out int length, out PacketAddress address, out int win32Error)
    {
        win32Error = 0;

        try
        {
            var (packet, packetAddress) = _incoming.Take();
            packet.CopyTo(buffer, 0);
            length = packet.Length;
            address = packetAddress;
            return true;
        }
        catch (InvalidOperationException)
        {
            length = 0;
            address = default;
            return false;
        }
    }

    public bool Send(ReadOnlySpan<byte> packet, in PacketAddress address, out int win32Error)
    {
        SentPackets.Enqueue((packet.ToArray(), address));

        win32Error = SendFailureWin32Error;
        return SendFailureWin32Error == 0;
    }

    public void Dispose() => _incoming.CompleteAdding();
}

internal sealed class FakeWinDivertSocketEvents : IWinDivertSocketEvents
{
    private readonly BlockingCollection<SocketEvent> _events = new();

    public void Enqueue(SocketEvent socketEvent) => _events.Add(socketEvent);

    public bool TryReceive(out SocketEvent socketEvent, out int win32Error)
    {
        win32Error = 0;

        try
        {
            socketEvent = _events.Take();
            return true;
        }
        catch (InvalidOperationException)
        {
            socketEvent = default;
            return false;
        }
    }

    public void Dispose() => _events.CompleteAdding();
}

internal sealed class FakeWinDivertHandleFactory(FakeWinDivertHandle handle, FakeWinDivertSocketEvents socketEvents)
    : IWinDivertHandleFactory
{
    public string? LastNetworkFilter { get; private set; }

    public string? LastSocketFilter { get; private set; }

    public Exception? NetworkOpenFailure { get; set; }

    public IWinDivertHandle OpenNetwork(string filter)
    {
        LastNetworkFilter = filter;

        if (NetworkOpenFailure is { } failure)
        {
            throw failure;
        }

        return handle;
    }

    public IWinDivertSocketEvents OpenSocketEvents(string filter)
    {
        LastSocketFilter = filter;
        return socketEvents;
    }
}
