using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.StateStore;

public sealed class FileConnectionStateStore : IConnectionStateStore
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new() { WriteIndented = true };
    private readonly string _stateFilePath;
    private readonly ILogger<FileConnectionStateStore> _logger;

    public FileConnectionStateStore(ILogger<FileConnectionStateStore> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProxyDiscord"))
    {
    }

    internal FileConnectionStateStore(ILogger<FileConnectionStateStore> logger, string directory)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _stateFilePath = Path.Combine(directory, "state.json");
    }

    public async Task WriteActiveStateAsync(ConnectionStateRecord record, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(record, JSON_OPTIONS);
        await File.WriteAllTextAsync(_stateFilePath, json, cancellationToken);
    }

    public Task ClearStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Não foi possível remover o arquivo de estado {Path}", _stateFilePath);
        }

        return Task.CompletedTask;
    }

    public async Task<ConnectionStateRecord?> TryReadStaleStateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_stateFilePath, cancellationToken);
            return JsonSerializer.Deserialize<ConnectionStateRecord>(json, JSON_OPTIONS);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Arquivo de estado corrompido em {Path}; ignorando", _stateFilePath);
            return null;
        }
    }
}
