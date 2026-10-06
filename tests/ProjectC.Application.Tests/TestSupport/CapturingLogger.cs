using Microsoft.Extensions.Logging;

namespace ProjectC.Application.Tests.TestSupport;

// 攔截 log 並可切換 Debug 開關，供「Debug 關閉時不呼叫 Log」與「log 欄位不含個資」的斷言使用
// （order-placement-p95-optimization LT-MEASURE-001～004）。Information 以上一律視為開啟，比照預設設定。
public sealed class CapturingLogger<T> : ILogger<T>
{
    public bool IsDebugEnabled { get; set; }

    public List<CapturedLogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information || (IsDebugEnabled && logLevel == LogLevel.Debug);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
            : new Dictionary<string, object?>();
        Entries.Add(new CapturedLogEntry(logLevel, formatter(state, exception), properties, exception));
    }
}

public sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);
