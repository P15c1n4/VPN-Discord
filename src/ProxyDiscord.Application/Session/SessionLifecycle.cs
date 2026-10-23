namespace ProxyDiscord.Application.Session;

internal sealed class SessionLifecycle
{
    private readonly object _lock = new();
    private CancellationTokenSource? _attempt;
    private int _disconnectRequests;
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal bool TrySetAttempt(CancellationTokenSource attempt)
    {
        lock (_lock)
        {
            if (_disconnectRequests != 0) return false;
            _attempt = attempt;
            return true;
        }
    }

    internal void ClearAttempt() { lock (_lock) _attempt = null; }
    internal void FinishDisconnect() { lock (_lock) _disconnectRequests--; }

    internal async Task CancelAttemptAsync()
    {
        CancellationTokenSource? attempt;
        lock (_lock)
        {
            _disconnectRequests++;
            attempt = _attempt;
        }
        if (attempt is null) return;
        try { await attempt.CancelAsync(); }
        catch (ObjectDisposedException) { }
    }
}
