using Microsoft.Extensions.Logging;

namespace ProjectC.WebApi.Tests.TestSupport;

// 輕量假 ILogger 實作，只記錄被呼叫過的等級，供斷言 LogWarning 是否確實被記錄（PQLE-007）。
// 獨立於 ProjectC.Infrastructure.Tests.TestSupport.RecordingLogger 的平行實作，理由同 RedisFixture
// ——不跨測試專案共用 TestSupport 類別。
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> LoggedLevels { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        LoggedLevels.Add(logLevel);
    }
}
