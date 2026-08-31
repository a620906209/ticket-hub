using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.TestSupport;

// 供測試捕捉實際輸出的 LogEvent 並斷言其結構化屬性（不只渲染後訊息文字），驗證 TraceId 關聯、
// 敏感資訊未外洩等行為（observability tasks.md 4.1）。ConcurrentBag：請求管線內的日誌可能來自
// 多個執行緒（例如背景服務與 HTTP 請求並行），須執行緒安全。
public sealed class InMemoryLogEventSink : ILogEventSink
{
    private readonly ConcurrentBag<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent)
    {
        _events.Add(logEvent);
    }

    public void Clear()
    {
        _events.Clear();
    }
}
