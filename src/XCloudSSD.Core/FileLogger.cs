using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace XCloudSSD.Core;

/// <summary>Small rolling text-file logger (operational logs only; never file content).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly string _prefix;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Thread _writer;
    public LogLevel MinLevel { get; set; } = LogLevel.Information;

    public FileLoggerProvider(string dir, string prefix)
    {
        _dir = dir;
        _prefix = prefix;
        Directory.CreateDirectory(dir);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "xcloudssd-log" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var file = Path.Combine(_dir, $"{_prefix}-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                CleanupOld();
            }
            catch
            {
                // Logging must never take the filesystem down.
            }
        }
    }

    private DateTime _lastCleanup = DateTime.MinValue;

    private void CleanupOld()
    {
        if ((DateTime.UtcNow - _lastCleanup).TotalHours < 6) return;
        _lastCleanup = DateTime.UtcNow;
        foreach (var f in Directory.GetFiles(_dir, $"{_prefix}-*.log"))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.MinLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("O")).Append(" [").Append(logLevel.ToString()[..4].ToUpperInvariant())
              .Append("] ").Append(category).Append(": ").Append(formatter(state, exception));
            if (exception is not null) sb.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            owner.Enqueue(sb.ToString());
        }
    }
}
