using NSubstitute;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Application.UseCases;
using ProxyDiscord.Domain.Entities;

namespace ProxyDiscord.Application.Tests;

public class DiscoverRunningProcessesUseCaseTests
{
    private readonly IProcessRepository _repository = Substitute.For<IProcessRepository>();

    private DiscoverRunningProcessesUseCase CreateUseCase() => new(_repository);

    [Fact]
    public async Task ExecuteAsync_ReturnsProcessesSortedByNameCaseInsensitive()
    {
        _repository.GetRunningProcessesAsync(Arg.Any<CancellationToken>()).Returns(new List<ProcessInfo>
        {
            new(3, "zoom", null),
            new(1, "Discord", null),
            new(2, "chrome", null),
        });

        var result = await CreateUseCase().ExecuteAsync();

        Assert.Equal(["chrome", "Discord", "zoom"], result.Select(p => p.Name));
    }

    [Fact]
    public async Task ExecuteAsync_FiltersOutProcessesWithoutAName()
    {
        _repository.GetRunningProcessesAsync(Arg.Any<CancellationToken>()).Returns(new List<ProcessInfo>
        {
            new(1, "Discord", null),
            new(2, "", null),
            new(3, "   ", null),
        });

        var result = await CreateUseCase().ExecuteAsync();

        Assert.Single(result);
        Assert.Equal("Discord", result[0].Name);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyRepository_ReturnsEmptyList()
    {
        _repository.GetRunningProcessesAsync(Arg.Any<CancellationToken>()).Returns(new List<ProcessInfo>());

        var result = await CreateUseCase().ExecuteAsync();

        Assert.Empty(result);
    }
}
