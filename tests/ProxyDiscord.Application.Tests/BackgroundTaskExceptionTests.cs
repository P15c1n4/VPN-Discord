using Microsoft.Extensions.Logging;
using ProxyDiscord.Presentation.Wpf;
using Xunit;

namespace ProxyDiscord.Application.Tests;

public sealed class BackgroundTaskExceptionTests
{
    [Fact]
    public void CancellationIsObservedAndLoggedAsDebug()
    {
        var exception = new AggregateException(new AggregateException(new OperationCanceledException()));
        var args = new UnobservedTaskExceptionEventArgs(exception);
        var logger = new RecordingLogger();

        App.ObserveBackgroundTaskException(args, logger);

        Assert.True(args.Observed);
        Assert.Equal(LogLevel.Debug, logger.Level);
        Assert.Same(exception, logger.Exception);
    }

    [Fact]
    public void RealFailureIsStillLoggedWithTheFullException()
    {
        var exception = new AggregateException(new OperationCanceledException(), new IOException("Socket failed"));
        var args = new UnobservedTaskExceptionEventArgs(exception);
        var logger = new RecordingLogger();

        App.ObserveBackgroundTaskException(args, logger);

        Assert.True(args.Observed);
        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.Same(exception, logger.Exception);
    }

    [Fact]
    public void FailureIsObservedEvenBeforeTheLoggerIsAvailable()
    {
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new IOException("Socket failed")));

        App.ObserveBackgroundTaskException(args, null);

        Assert.True(args.Observed);
    }

    private sealed class RecordingLogger : ILogger
    {
        public LogLevel? Level { get; private set; }
        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Exception = exception;
        }
    }
}
