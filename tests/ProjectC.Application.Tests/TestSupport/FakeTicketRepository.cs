using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeTicketRepository : ITicketRepository
{
    public List<Ticket> Data { get; } = new();

    public void Add(Ticket ticket) => Data.Add(ticket);

    public Task<Ticket?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(ticket => ticket.Id == id));

    public Task<IReadOnlyList<Ticket>> GetByOrderItemIdsAsync(IReadOnlyList<Guid> orderItemIds, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Ticket>>(Data.Where(ticket => orderItemIds.Contains(ticket.OrderItemId)).ToList());

    // 假物件內沒有真正的資料庫鎖，直接回傳目前持有的參考；真正驗證悲觀鎖效果的並發情境
    // 留給 Infrastructure 層的 Testcontainers 整合測試（比照 FakeOrderRepository.ReloadAsync 的處理方式）。
    public Task<Ticket?> GetForUpdateAsync(Guid ticketId, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(t => t.Id == ticketId));
}
