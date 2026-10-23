using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Infrastructure.OpenVpn;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class TunnelAdapterReadinessTests
{
    [Theory]
    [InlineData("10.8.0.2", DuplicateAddressDetectionState.Preferred, true)]
    [InlineData("10.8.0.2", DuplicateAddressDetectionState.Tentative, false)]
    [InlineData("10.8.0.2", DuplicateAddressDetectionState.Duplicate, false)]
    [InlineData("10.8.0.3", DuplicateAddressDetectionState.Preferred, false)]
    [InlineData("0.0.0.0", DuplicateAddressDetectionState.Preferred, false)]
    [InlineData("169.254.1.2", DuplicateAddressDetectionState.Preferred, false)]
    [InlineData("127.0.0.1", DuplicateAddressDetectionState.Preferred, false)]
    public void OnlyTheExpectedUsableAddressIsAccepted(string address, DuplicateAddressDetectionState state, bool expected)
    {
        Assert.Equal(expected, TunnelAdapterReadiness.IsUsableAddress(IPAddress.Parse(address), state, "10.8.0.2"));
    }

    [Fact]
    public async Task AdapterMayBecomeReadyAfterAnInitialNetworkError()
    {
        var attempts = 0;
        await TunnelAdapterReadiness.WaitAsync(new VpnAdapterInfo("10.8.0.2", 9, 0), CancellationToken.None,
            _ => ++attempts == 1 ? throw new NetworkInformationException() : true,
            TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AdapterWaitHasATimeLimitAndCanBeCanceled()
    {
        var adapter = new VpnAdapterInfo("10.8.0.2", 9, 0);
        await Assert.ThrowsAsync<TimeoutException>(() => TunnelAdapterReadiness.WaitAsync(
            adapter, CancellationToken.None, _ => false, TimeSpan.FromMilliseconds(30)));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TunnelAdapterReadiness.WaitAsync(adapter, stop.Token, _ => false));
    }

    [Fact]
    public async Task ForcedShutdownWaitsUntilTheOwnedProcessHasExited()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("127.0.0.1");
        using var process = Process.Start(start)!;
        try
        {
            await OpenVpnConnection.WaitOrKillAsync(process, TimeSpan.FromMilliseconds(30), NullLogger.Instance);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
