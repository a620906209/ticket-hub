namespace ProjectC.Domain.Events;

public interface IEventSeatRepository
{
    Task<EventSeat?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>唯讀查詢，不鎖定，供瀏覽端點使用；跟 <see cref="GetForUpdateAsync"/> 是兩個不同用途的方法，不要混用。</summary>
    Task<IReadOnlyList<EventSeat>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>唯讀批次查詢，MUST no-tracking（不加鎖），供建立訂單前的存在性／所屬活動比對使用——
    /// 這個比對必須在任何 <see cref="GetForUpdateAsync"/> 鎖定發生前完成（見 ticket-ordering spec
    /// 「在嘗試鎖定或扣減任何項目之前，系統 MUST 先驗證...所有項目須全部屬於同一場活動」）。
    /// 跟 <see cref="GetForUpdateAsync"/> 是不同用途的方法，不要混用——這個方法刻意 no-tracking，
    /// 避免 EF Core identity resolution 讓後續 <see cref="GetForUpdateAsync"/> 拿到查前的舊快照
    /// （design.md 決策 3 對 TicketType 的同一個陷阱，這裡是座位版本）。<paramref name="eventSeatIds"/>
    /// 為空清單時 MUST 直接回傳空清單，不得執行任何資料庫查詢。</summary>
    Task<IReadOnlyList<EventSeat>> GetByIdsAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken);

    /// <summary>
    /// 一次查詢多個活動底下的所有座位，供 Admin 活動列表的售票狀況統計使用；唯讀不鎖定，跟
    /// <see cref="GetForUpdateAsync"/> 是不同用途的方法，不要混用。
    /// 實作 MUST 一併載入計算 <see cref="EventSeat.GetStatus"/> 所需的完整持久化欄位，不可用只包含
    /// 公開欄位的投影查詢（比照 <see cref="GetByIdAsync"/> 的既有約定）。<paramref name="eventIds"/>
    /// 為空清單時 MUST 直接回傳空清單，不得執行任何資料庫查詢。
    /// </summary>
    Task<IReadOnlyList<EventSeat>> GetByEventIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken);

    void AddRange(IEnumerable<EventSeat> eventSeats);

    /// <summary>
    /// 以資料庫悲觀鎖（PostgreSQL <c>SELECT ... FOR UPDATE</c>）取得指定的座位，供修改鎖定/售出狀態前使用。
    /// <list type="bullet">
    /// <item>回傳的實體在目前交易內已被資料庫鎖定，直到交易 Commit 或 Rollback 才釋放。</item>
    /// <item>凡是會呼叫 <see cref="EventSeat.Hold"/>、<see cref="EventSeat.ConfirmSold"/>、<see cref="EventSeat.ReleaseHold"/>
    /// 任一方法之前，都必須先透過這個方法取得鎖，否則悲觀鎖形同虛設。</item>
    /// <item>MUST 在已開啟的資料庫交易內呼叫（見 <c>IUnitOfWork.BeginTransactionAsync</c>），否則拋出 <see cref="InvalidOperationException"/>——
    /// 沒有交易，PostgreSQL 的列鎖在查詢返回當下就已經釋放，呼叫這個方法不會有任何鎖定保護的效果。</item>
    /// <item><paramref name="eventSeatIds"/> 為空清單時拋出 <see cref="ArgumentException"/>；重複的 ID 由實作內部去重，呼叫端不需要先去重。</item>
    /// <item>部分 ID 找不到對應的 <see cref="EventSeat"/> 時，只回傳實際存在的實體，不補空值也不拋例外；
    /// 比對回傳數量是否符合預期，是呼叫端的責任。</item>
    /// </list>
    /// </summary>
    Task<IReadOnlyList<EventSeat>> GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken);
}
