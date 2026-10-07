using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeEventRepository : IEventRepository
{
    public List<Event> Data { get; } = new();

    // 呼叫次數與收到的 token／Id 供「查詢次數不隨筆數成長」與「token 原樣傳遞」的斷言使用
    // （order-display-enrichment tasks.md 1.1／1.2c）。
    public int GetByIdCallCount { get; private set; }
    public CancellationToken? LastGetByIdToken { get; private set; }
    public int GetByIdsCallCount { get; private set; }
    public int GetAllCallCount { get; private set; }
    public IReadOnlyList<Guid>? LastGetByIdsIds { get; private set; }
    public CancellationToken? LastGetByIdsToken { get; private set; }

    // 分別覆寫交易外 GetByIdAsync 與交易內 GetForUpdateAsync／GetForShareAsync 的回傳，模擬兩次讀取結果不同
    // （real-name-verification TP-RN-ORDER-007～009、PQ-RN-JOIN-007／008）。
    public Func<Guid, Event?>? GetByIdOverride { get; set; }
    public Func<Guid, Event?>? GetForUpdateOverride { get; set; }
    public Func<Guid, Event?>? GetForShareOverride { get; set; }

    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        GetByIdCallCount++;
        LastGetByIdToken = cancellationToken;
        return Task.FromResult(GetByIdOverride is null ? Data.FirstOrDefault(e => e.Id == id) : GetByIdOverride(id));
    }

    public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken)
    {
        GetAllCallCount++;
        return Task.FromResult<IReadOnlyList<Event>>(Data.ToList());
    }

    public Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
    {
        GetByIdsCallCount++;
        LastGetByIdsIds = eventIds.ToList();
        LastGetByIdsToken = cancellationToken;
        return Task.FromResult<IReadOnlyList<Event>>(Data.Where(e => eventIds.Contains(e.Id)).ToList());
    }

    public Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Event>>(Data.Where(e => e.OrganizerId == organizerId).ToList());

    public void Add(Event @event) => Data.Add(@event);

    // 比照 FakeTicketTypeRepository：Fake 不需要真的模擬鎖定，只回傳實際存在的實體。
    public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
        => Task.FromResult(GetForUpdateOverride is null ? Data.FirstOrDefault(e => e.Id == eventId) : GetForUpdateOverride(eventId));

    public Task<Event?> GetForShareAsync(Guid eventId, CancellationToken cancellationToken)
        => Task.FromResult(GetForShareOverride is null ? Data.FirstOrDefault(e => e.Id == eventId) : GetForShareOverride(eventId));

    // Event 是 reference type，Data 已持有同一實例，呼叫端對取得的實體所做的修改本來就反映在 Data 中；
    // 這裡不需要另外做任何事（比照 in-memory fake 對「標記為已修改」語意的既定簡化）。
    public void Update(Event @event)
    {
    }
}
