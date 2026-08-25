namespace ProjectC.Domain.Tickets;

public interface ITicketRepository
{
    void Add(Ticket ticket);

    Task<Ticket?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Ticket>> GetByOrderItemIdsAsync(IReadOnlyList<Guid> orderItemIds, CancellationToken cancellationToken);

    /// <summary>
    /// 悲觀鎖定單一 Ticket，用於核銷前的並發保護，比照 <see cref="ITicketTypeRepository.GetForUpdateAsync"/>／
    /// <see cref="Domain.Events.IEventSeatRepository.GetForUpdateAsync"/> 的既定模式，改為單筆查詢。
    /// <list type="bullet">
    /// <item>MUST 在既有的 <c>IUnitOfWork.BeginTransactionAsync</c> 交易內呼叫，否則列鎖一返回就釋放。</item>
    /// <item>查無資料時回傳 <c>null</c>，不拋例外，交由呼叫端判斷回 404。</item>
    /// <item>回傳的實體已是鎖定當下的最新狀態（單筆 <c>SELECT ... FOR UPDATE</c> 本身即完成「鎖定＋讀取」），
    /// 不像 <c>Order</c> 需要额外的 <c>ReloadAsync</c> 步驟——<c>Order</c> 本身不被鎖定，只有其關聯的
    /// <c>EventSeat</c>/<c>TicketType</c> 被鎖，才需要鎖後重讀；Ticket 核銷是直接鎖定 Ticket 自己，沒有這個落差。</item>
    /// </list>
    /// </summary>
    Task<Ticket?> GetForUpdateAsync(Guid ticketId, CancellationToken cancellationToken);
}
