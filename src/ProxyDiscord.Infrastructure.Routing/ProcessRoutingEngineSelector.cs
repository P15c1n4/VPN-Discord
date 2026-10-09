using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.ValueObjects;

namespace ProxyDiscord.Infrastructure.Routing;

public sealed class ProcessRoutingEngineSelector(
    ProcessRoutingEngine winDivert,
    ProxiFyreRoutingEngine proxiFyre) : IProcessRoutingEngine
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private IProcessRoutingEngine? _active;

    public bool IsRunning => _active?.IsRunning == true;
    public event EventHandler? TrafficObserved;
    public event EventHandler<RoutingEngineFailureEventArgs>? Failed;

    public async Task StartAsync(
        TargetProcessSelector target,
        VpnAdapterInfo vpnAdapter,
        TunnelDnsSettings dnsSettings,
        TunnelProtocolScope scope = TunnelProtocolScope.TcpAndUdp,
        ProcessRoutingBackend backend = ProcessRoutingBackend.WinDivert,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_active is not null)
            {
                throw new InvalidOperationException("Já existe um motor de roteamento ativo nesta sessão.");
            }

            var selected = backend switch
            {
                ProcessRoutingBackend.WinDivert => (IProcessRoutingEngine)winDivert,
                ProcessRoutingBackend.ProxiFyre => proxiFyre,
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Motor de roteamento não reconhecido."),
            };

            selected.TrafficObserved += OnTrafficObserved;
            selected.Failed += OnEngineFailed;
            _active = selected;
            try
            {
                await selected.StartAsync(target, vpnAdapter, dnsSettings, scope, backend, cancellationToken);
            }
            catch
            {
                selected.TrafficObserved -= OnTrafficObserved;
                selected.Failed -= OnEngineFailed;
                try { await selected.StopAsync(CancellationToken.None); }
                finally { _active = null; }
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var active = _active;
            if (active is null) return;

            await active.StopAsync(cancellationToken);
            active.TrafficObserved -= OnTrafficObserved;
            active.Failed -= OnEngineFailed;
            _active = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }

    private void OnTrafficObserved(object? sender, EventArgs args) => TrafficObserved?.Invoke(this, EventArgs.Empty);

    private void OnEngineFailed(object? sender, RoutingEngineFailureEventArgs args) => Failed?.Invoke(this, args);
}
