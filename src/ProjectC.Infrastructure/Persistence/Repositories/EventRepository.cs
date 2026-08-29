using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Events;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class EventRepository : IEventRepository
{
    private readonly ApplicationDbContext _dbContext;

    public EventRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // AsNoTracking：次要防護（belt-and-suspenders），比照 TicketTypeRepository.GetByIdAsync 相同理由——
    // 若交易前的讀取讓這筆 Event 被 identity map 追蹤住，交易內 GetForUpdateAsync 對同一主鍵的查詢會被
    // EF Core identity resolution 擋下、回傳鎖前的舊快照（rate-limiting-queue design.md 決策 4）。
    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken)
        => await _dbContext.Events.ToListAsync(cancellationToken);

    public void Add(Event @event) => _dbContext.Events.Add(@event);

    // GetByIdAsync 是 no-tracking，這裡用 DbSet.Update() 明確附加並標記為 Modified，
    // 讓一般欄位更新（例如 SetEventQueueModeHandler）能正常走 SaveChangesAsync（rate-limiting-queue design.md 決策 4）。
    public void Update(Event @event) => _dbContext.Events.Update(@event);

    // 主要防線：MUST 為 no-tracking，理由同上，且是本次「Queue Mode 切換線性化」正確性的充分條件
    // （design.md 決策 4）。EF Core 沒有對應 SELECT ... FOR UPDATE 的 LINQ API，改用 Raw SQL。
    public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
    {
        _dbContext.EnsureActiveTransaction(nameof(GetForUpdateAsync));

        return _dbContext.Events
            .FromSqlInterpolated($"""
                SELECT * FROM "Events"
                WHERE "Id" = {eventId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
    }
}
