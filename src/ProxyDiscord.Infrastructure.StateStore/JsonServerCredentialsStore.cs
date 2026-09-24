using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.StateStore;

public sealed class JsonServerCredentialsStore : IServerCredentialsStore
{
    private static readonly JsonSerializerOptions JSON_OPTIONS =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<JsonServerCredentialsStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonServerCredentialsStore(ILogger<JsonServerCredentialsStore> logger)
        : this(logger, AppContext.BaseDirectory)
    {
    }

    internal JsonServerCredentialsStore(ILogger<JsonServerCredentialsStore> logger, string directory)
    {
        _logger = logger;
        _filePath = Path.Combine(directory, "user_auth.json");
    }

    public async Task<ServerCredentials?> FindAsync(
        string serverKey,
        CancellationToken cancellationToken = default)
    {
        var document = await ReadDocumentAsync(cancellationToken);
        if (!document.Servers.TryGetValue(serverKey, out var entry))
        {
            return null;
        }

        var password = TryDecryptPassword(entry.ProtectedPassword, serverKey);
        return new ServerCredentials(entry.Username, password);
    }

    public async Task SaveAsync(
        string serverKey,
        string username,
        string? manuallyEnteredPassword,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            document.Servers.TryGetValue(serverKey, out var existing);

            var protectedPassword = ResolveProtectedPassword(existing, username, manuallyEnteredPassword, serverKey);
            document.Servers[serverKey] = new CredentialEntry(username, protectedPassword);
            await JsonFile.WriteAtomicallyAsync(_filePath, document, JSON_OPTIONS, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string? ResolveProtectedPassword(
        CredentialEntry? existing,
        string username,
        string? manuallyEnteredPassword,
        string serverKey)
    {
        if (!string.IsNullOrEmpty(manuallyEnteredPassword))
        {
            return WindowsDataProtection.Protect(manuallyEnteredPassword);
        }

        if (existing is null || !string.Equals(existing.Username, username, StringComparison.Ordinal))
        {
            return null;
        }

        return existing.ProtectedPassword;
    }

    private async Task<CredentialDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new CredentialDocument();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return JsonSerializer.Deserialize<CredentialDocument>(json, JSON_OPTIONS) ?? new CredentialDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Não foi possível ler as credenciais de {Path}; iniciando com arquivo vazio", _filePath);
            return new CredentialDocument();
        }
    }

    private string? TryDecryptPassword(string? protectedPassword, string serverKey)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword))
        {
            return null;
        }

        try
        {
            return WindowsDataProtection.Unprotect(protectedPassword);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Senha protegida inválida ou indisponível para o servidor {ServerKey}", serverKey);
            return null;
        }
    }

    private sealed class CredentialDocument
    {
        public CredentialDocument()
        {
        }

        public Dictionary<string, CredentialEntry> Servers { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CredentialEntry
    {
        public CredentialEntry()
        {
        }

        public CredentialEntry(string username, string? protectedPassword)
        {
            Username = username;
            ProtectedPassword = protectedPassword;
        }

        public string Username { get; set; } = "";

        public string? ProtectedPassword { get; set; }
    }
}
