using System.Net.NetworkInformation;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Connectivity;

public sealed class SystemPingService : IPingService
{
    public IAsyncEnumerable<PingResult> PingManyAsync(
        IReadOnlyList<HostProbe> targets,
        TimeSpan perItemTimeout,
        CancellationToken cancellationToken = default) =>
        ParallelProbeRunner.RunAsync(targets, (target, ct) => ProbeOneAsync(target, perItemTimeout, ct), cancellationToken);

    private static async Task<PingResult> ProbeOneAsync(HostProbe target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(target.Host, (int)timeout.TotalMilliseconds)
                .WaitAsync(timeout, cancellationToken);

            return reply.Status == IPStatus.Success
                ? new PingResult(target.Id, Success: true, TimeSpan.FromMilliseconds(reply.RoundtripTime), null)
                : new PingResult(target.Id, Success: false, null, reply.Status.ToString());
        }
        catch (TimeoutException)
        {
            return new PingResult(target.Id, Success: false, null, "Tempo limite excedido (3s)");
        }
        catch (PingException ex)
        {
            return new PingResult(target.Id, Success: false, null, ex.InnerException?.Message ?? ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PingResult(target.Id, Success: false, null, "Tempo limite excedido (3s)");
        }
    }
}
