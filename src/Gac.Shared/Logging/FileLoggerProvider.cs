using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Gac.Shared.Logging;

/// <summary>
/// Minimal rolling file logger (one file per day, 14 days kept). Local only, no network.
/// Lines are structured "time level category message".
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly string _prefix;
    private readonly LogLevel _min;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string directory, string prefix, LogLevel min = LogLevel.Information)
    {
        _dir = directory;
        _prefix = prefix;
        _min = min;
        try { Directory.CreateDirectory(_dir); } catch { }
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "GAC file logger" };
        _writer.Start();
        Cleanup();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(Path.Combine(_dir, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine, Encoding.UTF8); }
            catch { /* logging must never crash the service */ }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, _prefix + "-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._min && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception != null) msg += " | " + exception.GetType().Name + ": " + exception.Message;
            owner.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel,-11} {category}: {msg.ReplaceLineEndings(" ")}");
        }
    }
}
