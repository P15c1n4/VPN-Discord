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

    public void Start(TargetProcessSelector target)
    {
        lock (_lock)
        {
            _target = target;
            _trackedPids = [];
            _lastRefreshUtc = DateTime.MinValue;
            _lastLoggedCount = -1;
        }
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
        lock (_lock)
        {
            _target = null;
            _trackedPids = [];
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

        var pids = ResolveMatchingPids(target);

        lock (_lock)
        {
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
            return path is null || string.Equals(path, target.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
