using ProxyDiscord.Domain.ValueObjects;
using ProxyDiscord.Infrastructure.Routing;
using ProxyDiscord.Infrastructure.WinDivert;

namespace ProxyDiscord.Infrastructure.WinDivert.Tests;

public class WinDivertFilterCompilationTests
{
    [Fact]
    public void NetworkCaptureFilter_Compiles()
    {
        Assert.True(
            WinDivertFilterValidator.IsValid(
                ProcessRoutingEngine.CaptureFilterFor(TunnelProtocolScope.TcpAndUdp), WinDivertFilterLayer.Network, out var error),
            error);
    }

    [Fact]
    public void SocketEventFilter_Compiles()
    {
        Assert.True(
            WinDivertFilterValidator.IsValid(
                ProcessRoutingEngine.SOCKET_EVENT_FILTER, WinDivertFilterLayer.Socket, out var error),
            error);
    }

    [Fact]
    public void AnInvalidFilter_IsReportedWithItsPosition()
    {
        var isValid = WinDivertFilterValidator.IsValid(
            "outbound and nonsenseField == 1", WinDivertFilterLayer.Network, out var error);

        Assert.False(isValid);
        Assert.NotEmpty(error);
    }
}
