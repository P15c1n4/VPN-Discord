using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.StateStore;

/// <summary>Owns the portable application's single configuration/credential document.</summary>
public sealed class JsonApplicationDataStore : IUserConfigurationStore, IServerCredentialsStore
{
    private static readonly JsonSerializerOptions JSON_OPTIONS =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger<JsonApplicationDataStore> _logger;
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _legacyCredentialsPath;
    private readonly Func<string, string> _protectPassword;
    private readonly Func<string, string> _unprotectPassword;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DataDocument? _document;
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
        _legacyCredentialsPath = Path.Combine(_directory, "user_auth.json");
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
            var document = _document!;
            return new UserConfiguration(document.SaveCredentialsEnabled, document.SavePasswordAfterConnectionEnabled);
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
            var updated = Clone(_document!);
            updated.SaveCredentialsEnabled = configuration.SaveCredentialsEnabled;
            updated.SavePasswordAfterConnectionEnabled = configuration.SavePasswordAfterConnectionEnabled;
            await JsonFile.WriteAtomicallyAsync(_configPath, updated, JSON_OPTIONS, cancellationToken);
            _document = updated;
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
            var updated = Clone(_document!);
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
            await JsonFile.WriteAtomicallyAsync(_configPath, updated, JSON_OPTIONS, cancellationToken);
            _document = updated;
            _credentials[serverKey] = new ServerCredentials(username, TryDecrypt(protectedPassword, serverKey));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken cancellationToken)
    {
        if (_document is not null)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var document = await ReadConfigAsync(cancellationToken);
        var migrated = false;
        if (File.Exists(_legacyCredentialsPath))
        {
            try
            {
                var legacyJson = await File.ReadAllTextAsync(_legacyCredentialsPath, cancellationToken);
                var legacy = JsonSerializer.Deserialize<CredentialDocument>(legacyJson, JSON_OPTIONS);
                if (legacy is not null)
                {
                    foreach (var (key, value) in legacy.Servers)
                    {
                        if (!document.Servers.ContainsKey(key))
                        {
                            document.Servers[key] = value;
                            migrated = true;
                        }
                    }
                }

                if (migrated || !File.Exists(_configPath))
                {
                    await JsonFile.WriteAtomicallyAsync(_configPath, document, JSON_OPTIONS, cancellationToken);
                }

                File.Delete(_legacyCredentialsPath);
                _logger.LogInformation("Credenciais legadas migradas para config.json.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogError(ex, "Não foi possível migrar as credenciais legadas; o arquivo original foi mantido.");
                if (migrated)
                {
                    throw;
                }
            }
        }

        _document = document;
        _credentials = new Dictionary<string, ServerCredentials>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in document.Servers)
        {
            _credentials[key] = new ServerCredentials(entry.Username, TryDecrypt(entry.ProtectedPassword, key));
        }
    }

    private async Task<DataDocument> ReadConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configPath))
        {
            return new DataDocument();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath, cancellationToken);
            return JsonSerializer.Deserialize<DataDocument>(json, JSON_OPTIONS) ?? new DataDocument();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "config.json está inválido; os dados não serão sobrescritos automaticamente.");
            throw new InvalidDataException("O arquivo config.json está inválido. Preserve-o e corrija-o antes de continuar.", ex);
        }
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

    private static DataDocument Clone(DataDocument source) => new()
    {
        SaveCredentialsEnabled = source.SaveCredentialsEnabled,
        SavePasswordAfterConnectionEnabled = source.SavePasswordAfterConnectionEnabled,
        Servers = new Dictionary<string, CredentialEntry>(source.Servers, StringComparer.OrdinalIgnoreCase),
    };

    private sealed class DataDocument
    {
        public DataDocument() { }
        public bool SaveCredentialsEnabled { get; set; }
        public bool SavePasswordAfterConnectionEnabled { get; set; }
        public Dictionary<string, CredentialEntry> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
