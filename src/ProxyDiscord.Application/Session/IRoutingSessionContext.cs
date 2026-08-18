using System.ComponentModel;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Application.Session;

public interface IRoutingSessionContext : INotifyPropertyChanged
{
    ConnectionStatus Status { get; }
    ProcessInfo? TargetProcess { get; }
    TimeSpan? Latency { get; }
    string? LastError { get; }
}
