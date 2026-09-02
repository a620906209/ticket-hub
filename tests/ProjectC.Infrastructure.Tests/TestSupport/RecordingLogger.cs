using Microsoft.Extensions.Logging;

namespace ProjectC.Infrastructure.Tests.TestSupport;

// 輕量假 ILogger 實作，只記錄被呼叫過的等級，供斷言 LogWarning 是否確實被記錄（PQLE-007）。
// 不引入 Moq——這個測試專案原本沒有這個相依套件，比照 CLAUDE.md 簡化原則不為單一斷言新增依賴。
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
