using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Presentation.Wpf.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new(JsonSerializerDefaults.Web);
    private static readonly Regex SENSITIVE_ASSIGNMENT = new(
        @"(?i)(password|passwd|username|auth-token|access_token|refresh_token)(\s*[=:]\s*|\s+)[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Thread _writerThread;
    private readonly string _logFilePath;
    private readonly Func<object?> _appStateSnapshot;
    private readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _scope = new();

    public FileLoggerProvider(Func<object?>? appStateSnapshot = null)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(directory);
        MigrateLegacyLogs(directory);
        _logFilePath = Path.Combine(directory, $"app-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        _appStateSnapshot = appStateSnapshot ?? (() => null);

        _writerThread = new Thread(DrainQueue)
        {
            IsBackground = true,
            Name = "Discord-VPN log writer",
        };
        _writerThread.Start();
    }

    private static void MigrateLegacyLogs(string destination)
    {
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProxyDiscord", "logs");
        if (!Directory.Exists(legacy) ||
            string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var archive = Path.Combine(destination, "legacy");
        try
        {
            Directory.CreateDirectory(archive);
            foreach (var source in Directory.EnumerateFiles(legacy))
            {
                var target = Path.Combine(archive, Path.GetFileName(source));
                if (File.Exists(target))
                {
                    target = Path.Combine(archive, $"{Path.GetFileNameWithoutExtension(source)}-{Guid.NewGuid():N}{Path.GetExtension(source)}");
                }

                File.Move(source, target);
            }

            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The new logger continues in the executable directory; legacy records stay untouched if migration fails.
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line)
    {
        try
        {
            _queue.Add(line);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void DrainQueue()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider owner, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = owner._scope.Value;
            owner._scope.Value = ReadProperties(state);
            return new ScopeRestore(owner, previous);
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var properties = ReadProperties(state);
            var scope = owner._scope.Value;
            var request = GetProperty(scope, "request");
            var response = GetProperty(scope, "response");
            var scopeProperties = scope is null
                ? new Dictionary<string, object?>()
                : SanitizeProperties(scope.Where(pair =>
                    !pair.Key.Equals("request", StringComparison.OrdinalIgnoreCase) &&
                    !pair.Key.Equals("response", StringComparison.OrdinalIgnoreCase)));

            object? appState = null;
            try
            {
                appState = owner._appStateSnapshot();
            }
            catch
            {
                appState = new { unavailable = true };
            }

            var entry = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level = logLevel.ToString(),
                category = categoryName,
                eventId = new { id = eventId.Id, name = eventId.Name },
                message = SanitizeText(formatter(state, exception)),
                request = SanitizeValue(request),
                response = SanitizeValue(response),
                properties,
                scope = scopeProperties,
                exception = exception is null ? null : new
                {
                    type = exception.GetType().FullName,
                    message = SanitizeText(exception.Message),
                    stackTrace = exception.StackTrace,
                    fullDetails = SanitizeText(exception.ToString()),
                },
                appState = SanitizeValue(appState),
            };

            try
            {
                owner.Enqueue(JsonSerializer.Serialize(entry, JSON_OPTIONS));
            }
            catch (Exception serializationError) when (serializationError is NotSupportedException or JsonException)
            {
                owner.Enqueue(JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    level = logLevel.ToString(),
                    category = categoryName,
                    message = "Falha ao serializar evento de log.",
                    serializationError = serializationError.Message,
                    request = (object?)null,
                    response = (object?)null,
                    exception = exception is null ? null : SanitizeText(exception.ToString()),
                    appState = (object?)null,
                }, JSON_OPTIONS));
            }
        }

        private static Dictionary<string, object?> ReadProperties<T>(T state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values)
            {
                return new Dictionary<string, object?>();
            }

            return SanitizeProperties(values.Where(pair => pair.Key != "{OriginalFormat}"));
        }

        private static Dictionary<string, object?> SanitizeProperties(IEnumerable<KeyValuePair<string, object?>> values)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                result[pair.Key] = IsSensitiveKey(pair.Key) ? "[redigido]" : SanitizeValue(pair.Value);
            }

            return result;
        }

        private static object? GetProperty(IReadOnlyDictionary<string, object?>? values, string key) =>
            values is not null && values.TryGetValue(key, out var value) ? value : null;
    }

    private sealed class ScopeRestore(FileLoggerProvider owner, IReadOnlyDictionary<string, object?>? previous) : IDisposable
    {
        public void Dispose() => owner._scope.Value = previous;
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("username", StringComparison.OrdinalIgnoreCase);
    }

    private static object? SanitizeValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return SanitizeText(text);
        }

        if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return pairs.ToDictionary(
                pair => pair.Key,
                pair => IsSensitiveKey(pair.Key) ? "[redigido]" : SanitizeValue(pair.Value));
        }

        if (value.GetType().IsPrimitive || value is decimal or DateTime or DateTimeOffset or Guid or Enum)
        {
            return value;
        }

        return value.ToString() is { } description ? SanitizeText(description) : null;
    }

    private static string SanitizeText(string value) => SENSITIVE_ASSIGNMENT.Replace(value, "$1=[redigido]");
}
