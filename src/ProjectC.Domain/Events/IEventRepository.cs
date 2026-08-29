namespace ProjectC.Domain.Events;

public interface IEventRepository
{
    Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken);

    void Add(Event @event);

    /// <summary>將既有活動標記為已修改，供 <c>SetEventQueueModeHandler</c> 之類的一般欄位更新使用。</summary>
    void Update(Event @event);

    /// <summary>以 FOR UPDATE 鎖定並讀取活動，MUST 為 no-tracking（見 rate-limiting-queue design.md 決策 4）。</summary>
    Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken);
}
