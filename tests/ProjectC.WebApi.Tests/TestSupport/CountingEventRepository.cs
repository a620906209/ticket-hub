using ProjectC.Domain.Events;

namespace ProjectC.WebApi.Tests.TestSupport;

// 每個 HTTP 請求各自建立新的 scope、new 出新的 CountingEventRepository instance（IEventRepository
// 是 Scoped），計數器本身必須是跨 scope 共用的 Singleton，否則「呼叫兩次 API、驗證 Repository 只被
// 查一次」這種累計計數斷言會失真——每次都是新 instance、計數器歸零（query-caching tasks.md 第 2 節）。
public sealed class EventRepositoryCallCounter
{
    private int _getAllAsyncCallCount;

    public int GetAllAsyncCallCount => _getAllAsyncCallCount;

    public void RecordGetAllAsyncCall() => Interlocked.Increment(ref _getAllAsyncCallCount);
}

// 裝飾器：建構子注入真正的 EventRepository，每個方法直接轉呼叫真正的實作，行為完全不變
// （真的查資料庫），額外掛計數（透過共用的 Singleton 計數器）記錄呼叫次數。不是假物件，
// 只是多了一層可觀測性（query-caching tasks.md 第 2 節開頭；用於驗證快取命中時 Repository 未被重複查詢）。
public sealed class CountingEventRepository : IEventRepository
{
    private readonly IEventRepository _inner;
    private readonly EventRepositoryCallCounter _counter;

    public CountingEventRepository(IEventRepository inner, EventRepositoryCallCounter counter)
    {
        _inner = inner;
        _counter = counter;
    }

    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _inner.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken)
    {
        _counter.RecordGetAllAsyncCall();
        return _inner.GetAllAsync(cancellationToken);
    }

    public void Add(Event @event) => _inner.Add(@event);

    public void Update(Event @event) => _inner.Update(@event);

    public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
        => _inner.GetForUpdateAsync(eventId, cancellationToken);
}
