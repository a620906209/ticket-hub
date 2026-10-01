using Microsoft.Extensions.Logging;

namespace ProjectC.WebApi.Tests.TestSupport;

// 輕量假 ILogger 實作，記錄每筆 log 的等級、例外與格式化後訊息，供斷言 LogWarning 是否確實被記錄（PQLE-007），
// 以及全域例外處理記錄的例外內容與 TraceId（order-display-enrichment tasks.md 1.8）。
// 獨立於 ProjectC.Infrastructure.Tests.TestSupport.RecordingLogger 的平行實作，理由同 RedisFixture
// ——不跨測試專案共用 TestSupport 類別。
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<RecordedLogEntry> _entries = [];

    /// <summary>回傳目前記錄等級的快照；讀寫皆以同一把鎖保護，避免讀取時遇到並發寫入而拋出「集合已修改」。</summary>
    public IReadOnlyList<LogLevel> LoggedLevels
    {
        get
        {
            lock (_gate)
            {
                return _entries.Select(entry => entry.Level).ToList();
            }
        }
    }

    /// <summary>回傳目前記錄的快照；以鎖保護，因為註冊為 Singleton 時可能被多個請求同時寫入。</summary>
    public IReadOnlyList<RecordedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add(new RecordedLogEntry(logLevel, exception, formatter(state, exception)));
        }
    }
}

public sealed record RecordedLogEntry(LogLevel Level, Exception? Exception, string Message);
