using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Infrastructure.Connectivity;

public static class ParallelProbeRunner
{
    public static async IAsyncEnumerable<PingResult> RunAsync(
        IReadOnlyList<HostProbe> targets,
        Func<HostProbe, CancellationToken, Task<PingResult>> probe,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<PingResult>();

        _ = RunAllAsync(targets, probe, channel.Writer, cancellationToken);

        await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return result;
        }
    }

    private static async Task RunAllAsync(
        IReadOnlyList<HostProbe> targets,
        Func<HostProbe, CancellationToken, Task<PingResult>> probe,
        ChannelWriter<PingResult> writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            var tasks = targets.Select(target => ProbeAndPublishAsync(target, probe, writer, cancellationToken));
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private static async Task ProbeAndPublishAsync(
        HostProbe target,
        Func<HostProbe, CancellationToken, Task<PingResult>> probe,
        ChannelWriter<PingResult> writer,
        CancellationToken cancellationToken)
    {
        var result = await probe(target, cancellationToken);
        await writer.WriteAsync(result, cancellationToken);
    }
}
