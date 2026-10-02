using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeEventSeatRepository : IEventSeatRepository
{
    public List<EventSeat> Data { get; } = new();

    public Task<EventSeat?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(es => es.Id == id));

    public Task<IReadOnlyList<EventSeat>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<EventSeat>>(Data.Where(es => es.EventId == eventId).ToList());

    public Task<IReadOnlyList<EventSeat>> GetByEventIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<EventSeat>>(Data.Where(es => eventIds.Contains(es.EventId)).ToList());

    public int GetByIdsCallCount { get; private set; }
    public IReadOnlyList<Guid>? LastGetByIdsIds { get; private set; }
    public CancellationToken? LastGetByIdsToken { get; private set; }

    public Task<IReadOnlyList<EventSeat>> GetByIdsAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
    {
        GetByIdsCallCount++;
        LastGetByIdsIds = eventSeatIds.ToList();
        LastGetByIdsToken = cancellationToken;
        return Task.FromResult<IReadOnlyList<EventSeat>>(Data.Where(es => eventSeatIds.Contains(es.Id)).ToList());
    }

    public void AddRange(IEnumerable<EventSeat> eventSeats) => Data.AddRange(eventSeats);

    // 比照真正的 GetForUpdateAsync 契約：不驗證交易、找不到的不補，只回傳實際存在的實體
    // （見 IEventSeatRepository.cs 的說明；Fake 不需要真的模擬鎖定，OrderService 的測試只關心數量比對邏輯）。
    // 供「被擋下的請求不得鎖定任何座位」的斷言使用（real-name-verification TP-RN-ORDER-001／007～009）。
    public int GetForUpdateCallCount { get; private set; }

    public Task<IReadOnlyList<EventSeat>> GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
    {
        GetForUpdateCallCount++;
        return Task.FromResult<IReadOnlyList<EventSeat>>(Data.Where(es => eventSeatIds.Contains(es.Id)).ToList());
    }
}
