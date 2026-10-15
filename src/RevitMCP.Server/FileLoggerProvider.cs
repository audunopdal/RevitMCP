using Microsoft.Extensions.Logging;

namespace RevitMCP.Server;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _writer.WriteLine($"{DateTimeOffset.UtcNow:O} [{level}] {category}[{eventId.Id}] {message}");
            if (exception is not null)
                _writer.WriteLine(exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _writer.Dispose();
            _disposed = true;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(logLevel, category, eventId, formatter(state, exception), exception);
        }
    }
}
