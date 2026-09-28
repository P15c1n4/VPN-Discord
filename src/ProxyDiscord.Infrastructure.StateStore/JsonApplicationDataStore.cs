using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.StateStore;

/// <summary>Coordinates the portable configuration and per-server credential files.</summary>
public sealed class JsonApplicationDataStore : IUserConfigurationStore, IServerCredentialsStore
{
    private static readonly JsonSerializerOptions JSON_OPTIONS =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger<JsonApplicationDataStore> _logger;
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _credentialsPath;
    private readonly Func<string, string> _protectPassword;
    private readonly Func<string, string> _unprotectPassword;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConfigurationDocument? _configuration;
    private CredentialDocument? _credentialDocument;
    private Dictionary<string, ServerCredentials> _credentials = new(StringComparer.OrdinalIgnoreCase);

    public JsonApplicationDataStore(ILogger<JsonApplicationDataStore> logger, string? directory = null)
        : this(logger, directory, WindowsDataProtection.Protect, WindowsDataProtection.Unprotect)
    {
    }

    internal JsonApplicationDataStore(
        ILogger<JsonApplicationDataStore> logger,
        string? directory,
        Func<string, string> protectPassword,
        Func<string, string> unprotectPassword)
    {
        _logger = logger;
        _directory = directory ?? AppContext.BaseDirectory;
        _configPath = Path.Combine(_directory, "config.json");
        _credentialsPath = Path.Combine(_directory, "user_auth.json");
        _protectPassword = protectPassword;
        _unprotectPassword = unprotectPassword;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UserConfiguration> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            var configuration = _configuration!;
            return new UserConfiguration(configuration.SaveCredentialsEnabled, configuration.SavePasswordAfterConnectionEnabled);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(UserConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            var updated = new ConfigurationDocument
            {
                SaveCredentialsEnabled = configuration.SaveCredentialsEnabled,
                SavePasswordAfterConnectionEnabled = configuration.SavePasswordAfterConnectionEnabled,
            };
            await JsonFile.WriteAtomicallyAsync(_configPath, updated, JSON_OPTIONS, cancellationToken);
            _configuration = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerCredentials?> FindAsync(string serverKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            return _credentials.TryGetValue(serverKey, out var credentials) ? credentials : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string serverKey,
        string username,
        string? manuallyEnteredPassword,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedLockedAsync(cancellationToken);
            var updated = Clone(_credentialDocument!);
            updated.Servers.TryGetValue(serverKey, out var existing);

            string? protectedPassword;
            if (!string.IsNullOrEmpty(manuallyEnteredPassword))
            {
                protectedPassword = _protectPassword(manuallyEnteredPassword);
            }
            else if (existing is not null && string.Equals(existing.Username, username, StringComparison.Ordinal))
            {
                protectedPassword = existing.ProtectedPassword;
            }
            else
            {
                protectedPassword = null;
            }

            updated.Servers[serverKey] = new CredentialEntry(username, protectedPassword);
            await JsonFile.WriteAtomicallyAsync(_credentialsPath, updated, JSON_OPTIONS, cancellationToken);
            _credentialDocument = updated;
            _credentials[serverKey] = new ServerCredentials(username, TryDecrypt(protectedPassword, serverKey));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken cancellationToken)
    {
        if (_configuration is not null)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var (configuration, embeddedCredentials) = await ReadConfigurationAsync(cancellationToken);
        var credentials = await ReadCredentialDocumentAsync(cancellationToken);

        if (embeddedCredentials is not null)
        {
            foreach (var (key, entry) in embeddedCredentials.Servers)
            {
                if (credentials.Servers.TryGetValue(key, out var existing) &&
                    (!string.Equals(existing.Username, entry.Username, StringComparison.Ordinal) ||
                     !string.Equals(existing.ProtectedPassword, entry.ProtectedPassword, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        $"Há credenciais diferentes para '{key}' em config.json e user_auth.json. " +
                        "Os arquivos foram preservados para evitar perda de dados.");
                }

                credentials.Servers[key] = entry;
            }

            // If the second write fails, config.json still contains the source credentials.
            // Repeating this merge on the next startup is safe because identical entries are accepted.
            await JsonFile.WriteAtomicallyAsync(_credentialsPath, credentials, JSON_OPTIONS, cancellationToken);
            await JsonFile.WriteAtomicallyAsync(_configPath, configuration, JSON_OPTIONS, cancellationToken);
            _logger.LogInformation("Credenciais transferidas de config.json para user_auth.json.");
        }

        _configuration = configuration;
        _credentialDocument = credentials;
        _credentials = new Dictionary<string, ServerCredentials>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in credentials.Servers)
        {
            _credentials[key] = new ServerCredentials(entry.Username, TryDecrypt(entry.ProtectedPassword, key));
        }
    }

    private async Task<(ConfigurationDocument Configuration, CredentialDocument? EmbeddedCredentials)>
        ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configPath))
        {
            return (new ConfigurationDocument(), null);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath, cancellationToken);
            using var root = JsonDocument.Parse(json);
            if (root.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A configuração precisa ser um objeto JSON.");
            }

            var configuration = JsonSerializer.Deserialize<ConfigurationDocument>(json, JSON_OPTIONS)
                                ?? throw new JsonException("O documento de configuração está vazio.");
            var hasEmbeddedCredentials = false;
            JsonElement servers = default;
            foreach (var property in root.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("servers", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                hasEmbeddedCredentials = true;
                servers = property.Value;
            }

            if (!hasEmbeddedCredentials)
            {
                return (configuration, null);
            }

            if (servers.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A lista de servidores em config.json não é válida.");
            }

            var embedded = JsonSerializer.Deserialize<CredentialDocument>(json, JSON_OPTIONS)
                           ?? throw new JsonException("As credenciais em config.json não são válidas.");
            NormalizeCredentials(embedded, "config.json");
            return (configuration, embedded);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "config.json está inválido; o arquivo foi mantido.");
            throw new InvalidDataException("O arquivo config.json está inválido. Preserve-o e corrija-o antes de continuar.", ex);
        }
    }

    private async Task<CredentialDocument> ReadCredentialDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_credentialsPath))
        {
            return new CredentialDocument();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_credentialsPath, cancellationToken);
            using var root = JsonDocument.Parse(json);
            if (root.RootElement.ValueKind != JsonValueKind.Object ||
                !root.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals("servers", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object))
            {
                throw new JsonException("user_auth.json não contém uma lista válida de servidores.");
            }

            var document = JsonSerializer.Deserialize<CredentialDocument>(json, JSON_OPTIONS)
                           ?? throw new JsonException("O documento de credenciais está vazio.");
            NormalizeCredentials(document, "user_auth.json");
            return document;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "user_auth.json está inválido; o arquivo foi mantido.");
            throw new InvalidDataException("O arquivo user_auth.json está inválido. Preserve-o e corrija-o antes de continuar.", ex);
        }
    }

    private static void NormalizeCredentials(CredentialDocument document, string fileName)
    {
        if (document.Servers is null)
        {
            throw new JsonException($"A lista de servidores em {fileName} não é válida.");
        }

        var servers = new Dictionary<string, CredentialEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in document.Servers)
        {
            if (entry is null || entry.Username is null || !servers.TryAdd(key, entry))
            {
                throw new JsonException($"A credencial do servidor '{key}' em {fileName} é inválida ou duplicada.");
            }
        }

        document.Servers = servers;
    }

    private string? TryDecrypt(string? protectedPassword, string serverKey)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword))
        {
            return null;
        }

        try
        {
            return _unprotectPassword(protectedPassword);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Senha protegida indisponível para o servidor {ServerKey}.", serverKey);
            return null;
        }
    }

    private static CredentialDocument Clone(CredentialDocument source) => new()
    {
        Servers = new Dictionary<string, CredentialEntry>(source.Servers, StringComparer.OrdinalIgnoreCase),
    };

    private sealed class ConfigurationDocument
    {
        public ConfigurationDocument() { }
        public bool SaveCredentialsEnabled { get; set; }
        public bool SavePasswordAfterConnectionEnabled { get; set; }
    }

    private sealed class CredentialDocument
    {
        public CredentialDocument() { }
        public Dictionary<string, CredentialEntry> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CredentialEntry(string username, string? protectedPassword)
    {
        public CredentialEntry() : this("", null) { }
        public string Username { get; set; } = username;
        public string? ProtectedPassword { get; set; } = protectedPassword;
    }
}
