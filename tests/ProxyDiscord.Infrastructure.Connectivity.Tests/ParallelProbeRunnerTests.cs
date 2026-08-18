using System.Diagnostics;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Infrastructure.Connectivity;

namespace ProxyDiscord.Infrastructure.Connectivity.Tests;

public class ParallelProbeRunnerTests
{
    [Fact]
    public async Task RunAsync_MultipleTargets_RunsConcurrentlyNotSequentially()
    {
        var targets = Enumerable.Range(0, 5).Select(i => new HostProbe(i.ToString(), $"host{i}")).ToList();
        var probeDelay = TimeSpan.FromMilliseconds(200);

        var stopwatch = Stopwatch.StartNew();
        var results = new List<PingResult>();
        await foreach (var result in ParallelProbeRunner.RunAsync(targets, async (target, ct) =>
        {
            await Task.Delay(probeDelay, ct);
            return new PingResult(target.Id, true, probeDelay, null);
        }))
        {
            results.Add(result);
        }

        stopwatch.Stop();

        Assert.Equal(5, results.Count);
        Assert.True(stopwatch.Elapsed < probeDelay * 3, $"Levou {stopwatch.Elapsed} — parece sequencial, não paralelo.");
    }

    [Fact]
    public async Task RunAsync_OneSlowProbe_DoesNotDelayFasterResults()
    {
        var targets = new[] { new HostProbe("slow", "slow-host"), new HostProbe("fast", "fast-host") };
        var fastArrivedAt = TimeSpan.Zero;
        var stopwatch = Stopwatch.StartNew();

        await foreach (var result in ParallelProbeRunner.RunAsync(targets, async (target, ct) =>
        {
            if (target.Id == "slow")
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
            }

            return new PingResult(target.Id, true, TimeSpan.Zero, null);
        }))
        {
            if (result.Id == "fast")
            {
                fastArrivedAt = stopwatch.Elapsed;
            }
        }

        Assert.True(fastArrivedAt < TimeSpan.FromSeconds(1), $"O resultado rápido só chegou em {fastArrivedAt}.");
    }

    [Fact]
    public async Task RunAsync_ProbeTimesOut_YieldsFailureResultWithoutCancellingTheBatch()
    {
        var targets = new[] { new HostProbe("a", "host-a") };

        var results = new List<PingResult>();
        await foreach (var result in ParallelProbeRunner.RunAsync(targets, async (target, _) =>
        {
            using var itemTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), itemTimeoutCts.Token);
                return new PingResult(target.Id, true, TimeSpan.Zero, null);
            }
            catch (OperationCanceledException)
            {
                return new PingResult(target.Id, false, null, "Tempo limite excedido (3s)");
            }
        }))
        {
            results.Add(result);
        }

        Assert.Single(results);
        Assert.False(results[0].Success);
    }

    [Fact]
    public async Task RunAsync_ResultsStreamIncrementally_NotBatchedAtEnd()
    {
        var targets = new[] { new HostProbe("fast", "fast"), new HostProbe("slow", "slow") };
        var order = new List<string>();

        await foreach (var result in ParallelProbeRunner.RunAsync(targets, async (target, ct) =>
        {
            var delay = target.Id == "fast" ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromMilliseconds(300);
            await Task.Delay(delay, ct);
            return new PingResult(target.Id, true, delay, null);
        }))
        {
            order.Add(result.Id);
        }

        Assert.Equal(["fast", "slow"], order);
    }

    [Fact]
    public async Task RunAsync_EmptyTargetList_YieldsNoResults()
    {
        var results = new List<PingResult>();
        await foreach (var result in ParallelProbeRunner.RunAsync(
            Array.Empty<HostProbe>(), (_, _) => Task.FromResult(new PingResult("x", true, TimeSpan.Zero, null))))
        {
            results.Add(result);
        }

        Assert.Empty(results);
    }
}
