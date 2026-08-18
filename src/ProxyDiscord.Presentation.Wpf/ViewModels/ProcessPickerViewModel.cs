using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Domain.Entities;

namespace ProxyDiscord.Presentation.Wpf.ViewModels;

public sealed partial class ProcessPickerViewModel(
    DiscoverRunningProcessesUseCase discoverProcessesUseCase,
    ILogger<ProcessPickerViewModel> logger) : ObservableObject
{
    public ObservableCollection<ProcessGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private object? _selectedNode;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadError;

    partial void OnSelectedNodeChanged(object? value) => OnPropertyChanged(nameof(SelectedProcess));

    public ProcessInfo? SelectedProcess => SelectedNode switch
    {
        ProcessInfo process => process,
        ProcessGroupViewModel group => group.Representative,
        _ => null,
    };

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;

        try
        {
            var processes = await discoverProcessesUseCase.ExecuteAsync();

            Groups.Clear();
            foreach (var group in BuildGroups(processes))
            {
                Groups.Add(group);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao listar processos em execução");
            LoadError = $"Falha ao listar processos: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IEnumerable<ProcessGroupViewModel> BuildGroups(IReadOnlyList<ProcessInfo> processes) =>
        processes
            .GroupBy(p => string.IsNullOrWhiteSpace(p.ExecutablePath) ? p.Name : p.ExecutablePath!,
                     StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var instances = group.OrderBy(p => p.Pid).ToList();
                return new ProcessGroupViewModel(instances[0].Name, instances[0].ExecutablePath, instances);
            })
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase);
}
