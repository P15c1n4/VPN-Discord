using System.Diagnostics;
using ProxyDiscord.Infrastructure.ProcessManagement;

namespace ProxyDiscord.Infrastructure.ProcessManagement.Tests;

public class Win32ProcessRepositoryTests
{
    [Fact]
    public async Task GetRunningProcessesAsync_IncludesTheCurrentTestProcess()
    {
        var repository = new Win32ProcessRepository();
        var currentPid = Environment.ProcessId;

        var processes = await repository.GetRunningProcessesAsync();

        Assert.Contains(processes, p => p.Pid == currentPid);
    }

    [Fact]
    public async Task GetRunningProcessesAsync_NoDuplicatePids()
    {
        var repository = new Win32ProcessRepository();

        var processes = await repository.GetRunningProcessesAsync();

        var pids = processes.Select(p => p.Pid).ToList();
        Assert.Equal(pids.Count, pids.Distinct().Count());
    }

    [Fact]
    public async Task GetRunningProcessesAsync_ReturnsMoreThanOneProcess()
    {
        var repository = new Win32ProcessRepository();

        var processes = await repository.GetRunningProcessesAsync();

        Assert.True(processes.Count > 1, "Esperava múltiplos processos em execução.");
    }

    [Fact]
    public async Task GetRunningProcessesAsync_EveryEntryHasANonEmptyName()
    {
        var repository = new Win32ProcessRepository();

        var processes = await repository.GetRunningProcessesAsync();

        Assert.All(processes, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public async Task GetRunningProcessesAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        var repository = new Win32ProcessRepository();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetRunningProcessesAsync(cts.Token));
    }
}
