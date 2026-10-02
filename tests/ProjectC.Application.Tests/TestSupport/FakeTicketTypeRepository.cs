using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeTicketTypeRepository : ITicketTypeRepository
{
    public List<TicketType> Data { get; } = new();

    public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(t => t.Id == id));

    public int GetByEventIdCallCount { get; private set; }
    public CancellationToken? LastGetByEventIdToken { get; private set; }

    public Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
    {
        GetByEventIdCallCount++;
        LastGetByEventIdToken = cancellationToken;
        return Task.FromResult<IReadOnlyList<TicketType>>(Data.Where(t => t.EventId == eventId).ToList());
    }

    public void Add(TicketType ticketType) => Data.Add(ticketType);

    // 比照真正的 GetForUpdateAsync 契約：不驗證交易、找不到的不補，只回傳實際存在的實體
    // （見 ITicketTypeRepository.cs 的說明；Fake 不需要真的模擬鎖定，OrderService 的測試只關心數量比對邏輯）。
    // 供「被擋下的請求不得鎖定任何庫存」的斷言使用（real-name-verification TP-RN-ORDER-001／007～009）。
    public int GetForUpdateCallCount { get; private set; }

    public Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken)
    {
        GetForUpdateCallCount++;
        return Task.FromResult<IReadOnlyList<TicketType>>(Data.Where(t => ticketTypeIds.Contains(t.Id)).ToList());
    }
}
