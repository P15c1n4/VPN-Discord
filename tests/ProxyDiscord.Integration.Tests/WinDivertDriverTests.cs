using Microsoft.Extensions.DependencyInjection;
using ProxyDiscord.Infrastructure.Routing;
using ProxyDiscord.Infrastructure.WinDivert;

namespace ProxyDiscord.Integration.Tests;

[Trait("Category", "RequiresAdmin")]
public class WinDivertDriverTests
{
    private static IWinDivertHandleFactory CreateFactory()
    {
        var services = new ServiceCollection().AddWinDivertPacketCapture();
        return services.BuildServiceProvider().GetRequiredService<IWinDivertHandleFactory>();
    }

    [Fact]
    public void Open_TrivialNeverMatchFilter_SucceedsAndCloses()
    {
        using var handle = CreateFactory().OpenNetwork("false");

        Assert.NotNull(handle);
    }

    [Fact]
    public void OpenSocketEvents_SucceedsAndCloses()
    {
        using var events = CreateFactory().OpenSocketEvents("false");

        Assert.NotNull(events);
    }

    [Fact]
    public async Task TryReceive_OnAHandleClosedFromAnotherThread_UnblocksAndReturnsFalse()
    {
        var handle = CreateFactory().OpenNetwork("false");

        var receiveTask = Task.Run(() =>
        {
            var buffer = new byte[ushort.MaxValue];
            return handle.TryReceive(buffer, out _, out _, out _);
        });

        await Task.Delay(200);
        handle.Dispose();

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5))) == receiveTask;
        Assert.True(completed, "TryReceive não desbloqueou após o Dispose do handle.");
        Assert.False(await receiveTask);
    }
}
