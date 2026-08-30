using Microsoft.Extensions.Logging;

namespace ProjectC.Infrastructure.Tests.TestSupport;

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> State);

/// <summary>可收集記錄的測試 <see cref="ILogger{TCategoryName}"/>，供不方便用 <c>NullLogger</c> 的測試斷言 log 內容
/// （包含具名 placeholder 的實際值，不只是格式化後的訊息字串）。</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var namedValues = (state as IReadOnlyList<KeyValuePair<string, object?>>)?
            .ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object?>();
        _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, namedValues));
    }
}
