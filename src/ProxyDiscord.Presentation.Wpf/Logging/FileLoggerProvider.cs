using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ProxyDiscord.Presentation.Wpf.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private readonly Thread _writerThread;
    private readonly string _logFilePath;

    public FileLoggerProvider()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProxyDiscord", "logs");
        Directory.CreateDirectory(directory);
        _logFilePath = Path.Combine(directory, $"app-{DateTime.Now:yyyy-MM-dd}.log");

        _writerThread = new Thread(DrainQueue)
        {
            IsBackground = true,
            Name = "ProxyDiscord log writer",
        };
        _writerThread.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, Enqueue);

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
        var batch = new StringBuilder();

        foreach (var line in _queue.GetConsumingEnumerable())
        {
            batch.Clear().AppendLine(line);

            while (_queue.TryTake(out var extra))
            {
                batch.AppendLine(extra);
            }

            try
            {
                File.AppendAllText(_logFilePath, batch.ToString());
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

    private sealed class FileLogger(string categoryName, Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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

            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {categoryName}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            write(line);
        }
    }
}
