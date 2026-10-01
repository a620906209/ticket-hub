namespace ProjectC.Domain.Events;

public interface IEventRepository
{
    Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>唯讀批次查詢（no-tracking），供訂單列表取活動名稱，單次查詢、不隨筆數成長。
    /// 查不到的 Id 不回傳也不拋例外，比對缺失是呼叫端的責任；<paramref name="eventIds"/> 為空清單時
    /// MUST 直接回傳空清單，不得執行任何資料庫查詢。</summary>
    Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken);

    /// <summary>只回傳指定 Organizer 名下的活動；過濾 MUST 在資料庫端執行，不得把其他 Organizer 的活動
    /// 載入記憶體再過濾（見 event-management-organizer-scoping design.md「安全確認-資料庫」）。</summary>
    Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken);

    void Add(Event @event);

    /// <summary>將既有活動標記為已修改，供 <c>SetEventQueueModeHandler</c> 之類的一般欄位更新使用。</summary>
    void Update(Event @event);

    /// <summary>以 FOR UPDATE 鎖定並讀取活動，MUST 為 no-tracking（見 rate-limiting-queue design.md 決策 4）。</summary>
    Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken);
}
