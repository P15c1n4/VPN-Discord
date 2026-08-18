using System.ComponentModel;
using System.Runtime.CompilerServices;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Session;

public sealed class RoutingSessionContext : IRoutingSessionContext
{
    private ConnectionStatus _status = ConnectionStatus.Idle;
    private ProcessInfo? _targetProcess;
    private TimeSpan? _latency;
    private string? _lastError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectionStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public ProcessInfo? TargetProcess
    {
        get => _targetProcess;
        private set => SetField(ref _targetProcess, value);
    }

    public TimeSpan? Latency
    {
        get => _latency;
        private set => SetField(ref _latency, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetField(ref _lastError, value);
    }

    public void SetTargetProcess(ProcessInfo process) => TargetProcess = process;

    public void SetConnecting()
    {
        Status = ConnectionStatus.Connecting;
        LastError = null;
    }

    public void SetConnected(TimeSpan? latency)
    {
        Status = ConnectionStatus.Connected;
        Latency = latency;
        LastError = null;
    }

    public void UpdateLatency(TimeSpan latency) => Latency = latency;

    public void SetError(string message)
    {
        Status = ConnectionStatus.Error;
        LastError = message;
        Latency = null;
    }

    public void SetIdle()
    {
        Status = ConnectionStatus.Idle;
        Latency = null;
        LastError = null;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
