using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Domain.Entities;
using ProxyDiscord.Presentation.Wpf.ViewModels;

namespace ProxyDiscord.Presentation.Wpf.Tests;

public class ProcessPickerViewModelTests
{
    private readonly IProcessRepository _repository = Substitute.For<IProcessRepository>();

    private ProcessPickerViewModel CreateViewModel(params ProcessInfo[] processes)
    {
        _repository.GetRunningProcessesAsync(Arg.Any<CancellationToken>())
            .Returns(processes.ToList());

        return new ProcessPickerViewModel(
            new DiscoverRunningProcessesUseCase(_repository), NullLogger<ProcessPickerViewModel>.Instance);
    }

    [Fact]
    public async Task Load_InstancesOfOneExecutable_BecomeOneGroup()
    {
        var viewModel = CreateViewModel(
            new ProcessInfo(10, "Discord", @"C:\Discord\Discord.exe"),
            new ProcessInfo(11, "Discord", @"C:\Discord\Discord.exe"),
            new ProcessInfo(12, "Discord", @"C:\Discord\Discord.exe"));

        await viewModel.LoadCommand.ExecuteAsync(null);

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal("Discord", group.Name);
        Assert.Equal(3, group.Instances.Count);
        Assert.Equal("(3)", group.InstanceText);
    }

    [Fact]
    public async Task Load_SameNameDifferentPaths_StayApart()
    {
        var viewModel = CreateViewModel(
            new ProcessInfo(10, "updater", @"C:\AppA\updater.exe"),
            new ProcessInfo(11, "updater", @"C:\AppB\updater.exe"));

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Groups.Count);
        Assert.Equal(
            new[] { @"C:\AppA\updater.exe", @"C:\AppB\updater.exe" },
            viewModel.Groups.Select(g => g.ExecutablePath).Order().ToArray());
    }

    [Fact]
    public async Task Load_ProcessesWithoutAReadablePath_GroupByName()
    {
        var viewModel = CreateViewModel(
            new ProcessInfo(10, "csrss", null),
            new ProcessInfo(11, "csrss", null));

        await viewModel.LoadCommand.ExecuteAsync(null);

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal(2, group.Instances.Count);
        Assert.Equal("caminho indisponível", group.PathText);
    }

    [Fact]
    public async Task Load_SingleInstance_ShowsNoInstanceCount()
    {
        var viewModel = CreateViewModel(new ProcessInfo(10, "notepad", @"C:\Windows\notepad.exe"));

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("", Assert.Single(viewModel.Groups).InstanceText);
    }

    [Fact]
    public async Task Load_GroupsAreSortedByName()
    {
        var viewModel = CreateViewModel(
            new ProcessInfo(10, "zulu", @"C:\zulu.exe"),
            new ProcessInfo(11, "alpha", @"C:\alpha.exe"));

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "alpha", "zulu" }, viewModel.Groups.Select(g => g.Name).ToArray());
    }

    [Fact]
    public async Task SelectingTheGroup_ResolvesToAnInstance()
    {
        var viewModel = CreateViewModel(
            new ProcessInfo(20, "Discord", @"C:\Discord\Discord.exe"),
            new ProcessInfo(10, "Discord", @"C:\Discord\Discord.exe"));

        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.SelectedNode = viewModel.Groups[0];

        Assert.Equal(10, viewModel.SelectedProcess?.Pid);
        Assert.Equal(@"C:\Discord\Discord.exe", viewModel.SelectedProcess?.ExecutablePath);
    }

    [Fact]
    public async Task SelectingOneInstance_KeepsThatInstance()
    {
        var viewModel = CreateViewModel(
            new ProcessInfo(10, "Discord", @"C:\Discord\Discord.exe"),
            new ProcessInfo(11, "Discord", @"C:\Discord\Discord.exe"));

        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.SelectedNode = viewModel.Groups[0].Instances[1];

        Assert.Equal(11, viewModel.SelectedProcess?.Pid);
    }

    [Fact]
    public void SelectedProcess_WithNothingSelected_IsNull()
    {
        Assert.Null(CreateViewModel().SelectedProcess);
    }
}
