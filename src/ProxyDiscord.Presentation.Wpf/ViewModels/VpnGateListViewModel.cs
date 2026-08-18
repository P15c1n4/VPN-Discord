using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.UseCases;

namespace ProxyDiscord.Presentation.Wpf.ViewModels;

public enum VpnGateSortColumn
{
    Ping,
    HostName,
    Address,
    Port,
    Country,
    Score
}

public sealed partial class VpnGateListViewModel(
    FetchVpnGateListUseCase fetchListUseCase,
    TestServerLatenciesUseCase testLatenciesUseCase,
    Dispatcher dispatcher,
    ILogger<VpnGateListViewModel> logger) : ObservableObject
{
    private readonly List<VpnGateServerRowViewModel> _allServers = [];
    private VpnGateSortColumn? _sortColumn;
    private bool _sortAscending = true;

    public ObservableCollection<VpnGateServerRowViewModel> Servers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isLoadingList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isPinging;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _pingCompletedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _loadError;

    public bool IsBusy => IsLoadingList || IsPinging;

    public string StatusText
    {
        get
        {
            if (LoadError is not null)
            {
                return LoadError;
            }

            if (IsLoadingList)
            {
                return "carregando...";
            }

            return IsPinging
                ? $"({_allServers.Count}) {PingCompletedCount}/{_allServers.Count}"
                : $"({_allServers.Count})";
        }
    }

    public event Action<VpnGateServerEntry>? ServerSelected;

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoadingList = true;
        LoadError = null;
        PingCompletedCount = 0;
        _allServers.Clear();
        Servers.Clear();
        OnPropertyChanged(nameof(StatusText));

        IReadOnlyList<VpnGateServerEntry> entries;
        try
        {
            entries = await fetchListUseCase.ExecuteAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao obter a lista de servidores VPN Gate");
            LoadError = "falha ao carregar";
            IsLoadingList = false;
            return;
        }

        var rows = entries.Select(e => new VpnGateServerRowViewModel(e)).ToList();
        _allServers.AddRange(rows);
        ApplySort();

        IsLoadingList = false;
        OnPropertyChanged(nameof(StatusText));

        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

        _ = TestLatenciesAsync(entries, rows);
    }

    private async Task TestLatenciesAsync(IReadOnlyList<VpnGateServerEntry> entries, IReadOnlyList<VpnGateServerRowViewModel> rows)
    {
        var rowsByHost = rows.ToDictionary(r => r.HostName);
        IsPinging = true;
        PingCompletedCount = 0;

        try
        {
            await foreach (var result in testLatenciesUseCase.ExecuteAsync(entries))
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (rowsByHost.TryGetValue(result.Id, out var row))
                    {
                        row.ReportResult(result);
                    }

                    PingCompletedCount++;
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao testar latência dos servidores VPN Gate");
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                IsPinging = false;
                if (_sortColumn == VpnGateSortColumn.Ping)
                {
                    ApplySort();
                }
            });
        }
    }

    [RelayCommand]
    private void SelectServer(VpnGateServerRowViewModel row) => ServerSelected?.Invoke(row.Entry);

    [RelayCommand]
    private void SortByColumn(VpnGateSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        ApplySort();
    }

    private void ApplySort()
    {
        IEnumerable<VpnGateServerRowViewModel> ordered = _sortColumn switch
        {
            VpnGateSortColumn.Ping => _allServers.OrderBy(s => s.LatencyMs ?? int.MaxValue),
            VpnGateSortColumn.HostName => _allServers.OrderBy(s => s.HostName, StringComparer.OrdinalIgnoreCase),
            VpnGateSortColumn.Address => _allServers.OrderBy(s => s.Address, StringComparer.OrdinalIgnoreCase),
            VpnGateSortColumn.Port => _allServers.OrderBy(s => s.Port),
            VpnGateSortColumn.Country => _allServers.OrderBy(s => s.CountryLong, StringComparer.OrdinalIgnoreCase),
            VpnGateSortColumn.Score => _allServers.OrderBy(s => s.Score),
            _ => _allServers,
        };

        if (!_sortAscending)
        {
            ordered = ordered.Reverse();
        }

        Servers.Clear();
        foreach (var row in ordered)
        {
            Servers.Add(row);
        }
    }
}
