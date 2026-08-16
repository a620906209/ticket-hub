namespace ProjectC.Domain.Events;

public interface IEventSeatRepository
{
    Task<EventSeat?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

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
