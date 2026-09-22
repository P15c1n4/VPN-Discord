using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class ProcessGroupWatcher(ILogger<ProcessGroupWatcher> logger) : IProcessGroupWatcher
{
    private static readonly TimeSpan REFRESH_INTERVAL = TimeSpan.FromMilliseconds(750);

    private readonly object _lock = new();
    private TargetProcessSelector? _target;
    private HashSet<int> _trackedPids = [];
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private int _lastLoggedCount = -1;
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;

    public void Start(TargetProcessSelector target)
    {
        CancellationTokenSource? previousCts;
        CancellationTokenSource refreshCts;

        lock (_lock)
        {
            previousCts = _refreshCts;
            refreshCts = new CancellationTokenSource();
            _refreshCts = refreshCts;
            _target = target;
            _trackedPids = [];
            _lastRefreshUtc = DateTime.MinValue;
            _lastLoggedCount = -1;
            _refreshTask = Task.Run(() => RefreshLoopAsync(target, refreshCts, refreshCts.Token));
        }

        previousCts?.Cancel();
        previousCts?.Dispose();
    }

    public bool IsTracked(int pid)
    {
        EnsureFresh();
        lock (_lock)
        {
            return _trackedPids.Contains(pid);
        }
    }

    public int TrackedCount
    {
        get
        {
            EnsureFresh();
            lock (_lock)
            {
                return _trackedPids.Count;
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? refreshCts;
        Task? refreshTask;

        lock (_lock)
        {
            refreshCts = _refreshCts;
            _refreshCts = null;
            refreshTask = _refreshTask;
            _refreshTask = null;
            _target = null;
            _trackedPids = [];
        }

        refreshCts?.Cancel();

        if (refreshTask is not null)
        {
            try
            {
                refreshTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
            {
                logger.LogDebug(ex, "Monitor de processo terminou com erro durante a limpeza");
            }
        }

        refreshCts?.Dispose();
    }

    private async Task RefreshLoopAsync(
        TargetProcessSelector target,
        CancellationTokenSource expectedRefreshCts,
        CancellationToken cancellationToken)
    {
        try
        {
            RefreshNow(target, cancellationToken, expectedRefreshCts);

            using var timer = new PeriodicTimer(REFRESH_INTERVAL);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RefreshNow(target, cancellationToken, expectedRefreshCts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha no monitoramento contínuo do processo alvo '{Target}'.", target.DisplayName);
        }
    }

    private void EnsureFresh()
    {
        TargetProcessSelector? target;
        lock (_lock)
        {
            if (_target is null || DateTime.UtcNow - _lastRefreshUtc < REFRESH_INTERVAL)
            {
                return;
            }

            target = _target;
            _lastRefreshUtc = DateTime.UtcNow;
        }

        RefreshNow(target, cancellationToken: default, expectedRefreshCts: null);
    }

    private void RefreshNow(
        TargetProcessSelector target,
        CancellationToken cancellationToken,
        CancellationTokenSource? expectedRefreshCts)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pids = ResolveMatchingPids(target);

        lock (_lock)
        {
            if (_target is null || !ReferenceEquals(_target, target) ||
                (expectedRefreshCts is not null && !ReferenceEquals(_refreshCts, expectedRefreshCts)))
            {
                return;
            }

            _lastRefreshUtc = DateTime.UtcNow;
            _trackedPids = pids;
            if (pids.Count != _lastLoggedCount)
            {
                _lastLoggedCount = pids.Count;
                logger.LogInformation(
                    "Processo alvo '{Target}': {Count} processo(s) em execução sendo tunelado(s).",
                    target.DisplayName, pids.Count);
            }
        }
    }

    private HashSet<int> ResolveMatchingPids(TargetProcessSelector target)
    {
        var matches = new HashSet<int>();

        var processName = Path.GetFileNameWithoutExtension(
            string.IsNullOrWhiteSpace(target.ExecutablePath) ? target.ProcessName : target.ExecutablePath);

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(processName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao enumerar processos chamados '{Name}'", processName);
            return matches;
        }

        foreach (var process in candidates)
        {
            using (process)
            {
                if (MatchesTarget(process, target))
                {
                    matches.Add(process.Id);
                }
            }
        }

        return matches;
    }

    private static bool MatchesTarget(Process process, TargetProcessSelector target)
    {
        if (string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            return true;
        }

        try
        {
            var path = process.MainModule?.FileName;
            if (path is null)
            {
                return true;
            }

            var actualPath = Path.GetFullPath(path);
            var expectedPath = Path.GetFullPath(target.ExecutablePath);
            return string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
