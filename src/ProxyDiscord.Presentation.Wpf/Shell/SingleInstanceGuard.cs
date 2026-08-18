using System.Threading;

namespace ProxyDiscord.Presentation.Wpf.Shell;

internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MUTEX_NAME = @"Local\ProxyDiscord.SingleInstance";
    private const string ACTIVATION_EVENT_NAME = @"Local\ProxyDiscord.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Thread? _listener;
    private bool _disposed;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MUTEX_NAME, out var createdNew);
        IsPrimary = createdNew;

        if (IsPrimary)
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ACTIVATION_EVENT_NAME);
        }
    }

    public bool IsPrimary { get; }

    public event EventHandler? ActivationRequested;

    public void StartListening()
    {
        if (!IsPrimary || _activationEvent is null || _listener is not null)
        {
            return;
        }

        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = nameof(SingleInstanceGuard),
        };
        _listener.Start();
    }

    public static bool TrySignalRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(ACTIVATION_EVENT_NAME, out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    private void ListenLoop()
    {
        var waitHandles = new WaitHandle[] { _activationEvent!, _listenerCancellation.Token.WaitHandle };

        while (!_listenerCancellation.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(waitHandles) != 0)
            {
                return;
            }

            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _listenerCancellation.Cancel();
        _listener?.Join(TimeSpan.FromSeconds(1));
        _listenerCancellation.Dispose();
        _activationEvent?.Dispose();

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
    }
}
