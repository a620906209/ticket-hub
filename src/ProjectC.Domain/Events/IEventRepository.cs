namespace ProjectC.Domain.Events;

public interface IEventRepository
{
    Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>只回傳指定 Organizer 名下的活動；過濾 MUST 在資料庫端執行，不得把其他 Organizer 的活動
    /// 載入記憶體再過濾（見 event-management-organizer-scoping design.md「安全確認-資料庫」）。</summary>
    Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken);

    void Add(Event @event);

    /// <summary>將既有活動標記為已修改，供 <c>SetEventQueueModeHandler</c> 之類的一般欄位更新使用。</summary>
    void Update(Event @event);

    /// <summary>以 FOR UPDATE 鎖定並讀取活動，MUST 為 no-tracking（見 rate-limiting-queue design.md 決策 4）。</summary>
    Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken);
}
