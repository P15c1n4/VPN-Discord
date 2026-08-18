using ProxyDiscord.Infrastructure.Routing;

namespace ProxyDiscord.Infrastructure.WinDivert;

internal sealed class WinDivertHandleFactory : IWinDivertHandleFactory
{
    public IWinDivertHandle OpenNetwork(string filter) => new WinDivertHandle(filter);

    public IWinDivertSocketEvents OpenSocketEvents(string filter) => new WinDivertSocketEventHandle(filter);
}
