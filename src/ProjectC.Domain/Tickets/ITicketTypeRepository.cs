namespace ProjectC.Domain.Tickets;

public interface ITicketTypeRepository
{
    /// <summary>唯讀查詢，不加鎖（no-tracking）；純粹用於建立訂單前的存在性/RequiresSeat 比對。
    /// 跟 <see cref="GetForUpdateAsync"/> 是兩個不同用途的方法，不要混用——這個方法刻意不 tracking，
    /// 避免 EF Core identity resolution 讓後續 GetForUpdateAsync 拿到鎖前的舊快照（design.md 決策 3）。</summary>
    Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken);

    void Add(TicketType ticketType);

    /// <summary>
    /// 悲觀鎖定一組 TicketType，用於扣減/歸還 AvailableQuantity 前的並發保護，比照
    /// <see cref="Domain.Events.IEventSeatRepository.GetForUpdateAsync"/> 的既定模式。
    /// <list type="bullet">
    /// <item>MUST 在既有的 <c>IUnitOfWork.BeginTransactionAsync</c> 交易內呼叫，否則列鎖一返回就釋放。</item>
    /// <item><paramref name="ticketTypeIds"/> 為空清單時拋出 <see cref="ArgumentException"/>；重複的 ID 由實作內部去重，呼叫端不需要先去重。</item>
    /// <item>一次查詢鎖定所有列，依 Id 排序（資料庫端 ORDER BY，不靠 .NET 端排序），確保所有交易走同一條鎖定順序、避免死鎖——一筆訂單可能同時涉及多個不同的計數票種。</item>
    /// <item>部分 ID 找不到對應的 TicketType 時，只回傳實際存在的實體，不補空值也不拋例外；比對回傳數量是否符合預期，是呼叫端的責任。</item>
    /// </list>
    /// </summary>
    Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken);
}
