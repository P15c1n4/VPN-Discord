using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.StateStore;

public sealed class JsonUserConfigurationStore : IUserConfigurationStore
{
    private static readonly JsonSerializerOptions JSON_OPTIONS =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<JsonUserConfigurationStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonUserConfigurationStore(ILogger<JsonUserConfigurationStore> logger)
        : this(logger, AppContext.BaseDirectory)
    {
    }

    internal JsonUserConfigurationStore(ILogger<JsonUserConfigurationStore> logger, string directory)
    {
        _logger = logger;
        _filePath = Path.Combine(directory, "config.json");
    }

    public async Task<UserConfiguration> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return new UserConfiguration();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return JsonSerializer.Deserialize<UserConfiguration>(json, JSON_OPTIONS) ?? new UserConfiguration();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Não foi possível ler a configuração {Path}; usando opções padrão", _filePath);
            return new UserConfiguration();
        }
    }

    public async Task WriteAsync(
        UserConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await JsonFile.WriteAtomicallyAsync(_filePath, configuration, JSON_OPTIONS, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
