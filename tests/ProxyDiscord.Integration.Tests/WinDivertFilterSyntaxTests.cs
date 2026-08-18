using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Infrastructure.Routing;
using ProxyDiscord.Infrastructure.WinDivert;

namespace ProxyDiscord.Integration.Tests;

public class WinDivertFilterSyntaxTests
{
    [Theory]
    [InlineData(TunnelProtocolScope.TcpAndUdp)]
    [InlineData(TunnelProtocolScope.TcpOnly)]
    [InlineData(TunnelProtocolScope.UdpOnly)]
    public void CaptureFilter_OfEveryScope_IsAcceptedAtTheNetworkLayer(TunnelProtocolScope scope)
    {
        var filter = ProcessRoutingEngine.CaptureFilterFor(scope);

        var isValid = WinDivertFilterValidator.IsValid(filter, WinDivertFilterLayer.Network, out var error);

        Assert.True(isValid, $"Filtro inválido '{filter}': {error}");
    }

    [Fact]
    public void SocketEventFilter_IsAcceptedAtTheSocketLayer()
    {
        var isValid = WinDivertFilterValidator.IsValid(
            ProcessRoutingEngine.SOCKET_EVENT_FILTER, WinDivertFilterLayer.Socket, out var error);

        Assert.True(isValid, $"Filtro inválido '{ProcessRoutingEngine.SOCKET_EVENT_FILTER}': {error}");
    }

    [Fact]
    public void ProcessIdField_IsAvailableAtTheSocketLayer_WhichIsWhyTheAppUsesWinDivert2()
    {
        var isValid = WinDivertFilterValidator.IsValid(
            "processId == 1234", WinDivertFilterLayer.Socket, out var error);

        Assert.True(isValid, $"A camada SOCKET deveria aceitar processId: {error}");
    }

    [Fact]
    public void ProcessIdField_IsRejectedAtTheNetworkLayer_WhichIsWhyOwnershipIsTrackedSeparately()
    {
        var isValid = WinDivertFilterValidator.IsValid(
            "processId == 1234", WinDivertFilterLayer.Network, out _);

        Assert.False(isValid);
    }
}
