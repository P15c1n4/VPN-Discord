using Microsoft.Extensions.Logging.Abstractions;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Infrastructure.StateStore;

namespace ProxyDiscord.Infrastructure.StateStore.Tests;

public class FileConnectionStateStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "ProxyDiscordTests_" + Guid.NewGuid());

    private FileConnectionStateStore CreateStore() => new(NullLogger<FileConnectionStateStore>.Instance, _tempDirectory);

    private static readonly ConnectionStateRecord SAMPLE_RECORD = new(
        OwnerPid: 100, OwnerStartedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        TargetProcessId: 200, TargetProcessName: "Discord", RasEntryName: "ProxyDiscord-Discord-abc",
        CreatedUtc: new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));

    [Fact]
    public async Task TryReadStaleStateAsync_NoFileWritten_ReturnsNull()
    {
        var result = await CreateStore().TryReadStaleStateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsAllFields()
    {
        var store = CreateStore();

        await store.WriteActiveStateAsync(SAMPLE_RECORD);
        var read = await store.TryReadStaleStateAsync();

        Assert.Equal(SAMPLE_RECORD, read);
    }

    [Fact]
    public async Task ClearStateAsync_RemovesFile()
    {
        var store = CreateStore();
        await store.WriteActiveStateAsync(SAMPLE_RECORD);

        await store.ClearStateAsync();
        var read = await store.TryReadStaleStateAsync();

        Assert.Null(read);
    }

    [Fact]
    public async Task ClearStateAsync_WhenNoFileExists_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() => CreateStore().ClearStateAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task TryReadStaleStateAsync_CorruptedFile_ReturnsNullInsteadOfThrowing()
    {
        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "state.json"), "{ not valid json ");

        var result = await CreateStore().TryReadStaleStateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task WriteActiveStateAsync_OverwritesPreviousRecord()
    {
        var store = CreateStore();
        await store.WriteActiveStateAsync(SAMPLE_RECORD);

        var updated = SAMPLE_RECORD with { TargetProcessName = "OtherApp" };
        await store.WriteActiveStateAsync(updated);
        var read = await store.TryReadStaleStateAsync();

        Assert.Equal("OtherApp", read!.TargetProcessName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
