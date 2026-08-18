using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Events;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class EventSeatRepository : IEventSeatRepository
{
    private readonly ApplicationDbContext _dbContext;

    public EventSeatRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<EventSeat?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.EventSeats.FirstOrDefaultAsync(es => es.Id == id, cancellationToken);

    public async Task<IReadOnlyList<EventSeat>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        => await _dbContext.EventSeats.Where(es => es.EventId == eventId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EventSeat>> GetByEventIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
            return [];

        return await _dbContext.EventSeats.Where(es => eventIds.Contains(es.EventId)).ToListAsync(cancellationToken);
    }

    public void AddRange(IEnumerable<EventSeat> eventSeats) => _dbContext.EventSeats.AddRange(eventSeats);

    public async Task<IReadOnlyList<EventSeat>> GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
    {
        if (eventSeatIds.Count == 0)
            throw new ArgumentException("At least one event seat id must be provided.", nameof(eventSeatIds));

        // PostgreSQL 的列鎖只在交易存續期間有效；沒有進行中的交易時，SELECT ... FOR UPDATE
        // 一返回鎖就釋放了，等於完全沒有鎖定保護，所以在這裡 fail fast（design.md 決策 2）。
        if (_dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"{nameof(GetForUpdateAsync)} must be called within an active transaction " +
                "(see IUnitOfWork.BeginTransactionAsync); otherwise the row lock is released " +
                "as soon as this query returns.");
        }

        // 去重是這個方法自己的責任，不信任呼叫端一定會先去重（design.md 決策 1）。
        var distinctIds = eventSeatIds.Distinct().ToArray();

        // EF Core 沒有對應 SELECT ... FOR UPDATE 的 LINQ API，這裡改用 Raw SQL（CLAUDE.md 允許的例外情況）。
        // 一次查詢鎖定所有列，不逐筆迴圈；鎖定順序由資料庫的 ORDER BY 保證，不靠 .NET 端排序，
        // 確保所有交易走同一條鎖定順序、避免死鎖（design.md 決策 1）。
        return await _dbContext.EventSeats
            .FromSqlInterpolated($"""
                SELECT * FROM "EventSeats"
                WHERE "Id" = ANY({distinctIds})
                ORDER BY "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
    }
}
