using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeEventRepository : IEventRepository
{
    public List<Event> Data { get; } = new();

    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(e => e.Id == id));

    public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Event>>(Data.ToList());

    public void Add(Event @event) => Data.Add(@event);

    // 比照 FakeTicketTypeRepository：Fake 不需要真的模擬鎖定，只回傳實際存在的實體。
    public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(e => e.Id == eventId));

    // Event 是 reference type，Data 已持有同一實例，呼叫端對取得的實體所做的修改本來就反映在 Data 中；
    // 這裡不需要另外做任何事（比照 in-memory fake 對「標記為已修改」語意的既定簡化）。
    public void Update(Event @event)
    {
    }
}
