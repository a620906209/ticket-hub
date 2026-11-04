using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.TestSupport;

// 供測試捕捉實際輸出的 LogEvent 並斷言其結構化屬性（不只渲染後訊息文字），驗證 TraceId 關聯、
// 敏感資訊未外洩等行為（observability tasks.md 4.1）。ConcurrentBag：請求管線內的日誌可能來自
// 多個執行緒（例如背景服務與 HTTP 請求並行），須執行緒安全。
public sealed class InMemoryLogEventSink : ILogEventSink, IDisposable
{
    private readonly ConcurrentBag<LogEvent> _events = new();
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 內層 logger 依掛載順序 dispose sink（Seq 先、此 sink 後）。host 關機時 WebApplicationFactory.DisposeAsync
    // 回傳不代表 logger 已 dispose 完：Host.RunAsync 那條執行緒也在 dispose，兩邊競爭、後者可能晚幾毫秒才做完。
    // 要等「Seq 最後一批已 flush」的測試等這個 Task，不要用固定延遲或輪詢。
    public Task Disposed => _disposed.Task;

    public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent)
    {
        _events.Add(logEvent);
    }

    public void Dispose()
    {
        _disposed.TrySetResult();
    }

    public void Clear()
    {
        _events.Clear();
    }
}
