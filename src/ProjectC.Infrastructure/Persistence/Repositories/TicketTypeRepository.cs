using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Tickets;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class TicketTypeRepository : ITicketTypeRepository
{
    private readonly ApplicationDbContext _dbContext;

    public TicketTypeRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // AsNoTracking：這是 OrderService.PlaceOrderAsync 交易前存在性檢查的唯一呼叫端，若改成 tracking，
    // 交易內 GetForUpdateAsync 對同一主鍵的查詢會被 EF Core identity resolution 擋下、回傳鎖前的舊快照
    // （design.md 決策 3）。
    public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.TicketTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        => await _dbContext.TicketTypes.Where(t => t.EventId == eventId).ToListAsync(cancellationToken);

    public void Add(TicketType ticketType) => _dbContext.TicketTypes.Add(ticketType);

    public async Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken)
    {
        if (ticketTypeIds.Count == 0)
            throw new ArgumentException("At least one ticket type id must be provided.", nameof(ticketTypeIds));

        // PostgreSQL 的列鎖只在交易存續期間有效；沒有進行中的交易時，SELECT ... FOR UPDATE
        // 一返回鎖就釋放了，等於完全沒有鎖定保護，所以在這裡 fail fast（比照 EventSeatRepository）。
        if (_dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"{nameof(GetForUpdateAsync)} must be called within an active transaction " +
                "(see IUnitOfWork.BeginTransactionAsync); otherwise the row lock is released " +
                "as soon as this query returns.");
        }

        // 去重是這個方法自己的責任，不信任呼叫端一定會先去重。
        var distinctIds = ticketTypeIds.Distinct().ToArray();

        // EF Core 沒有對應 SELECT ... FOR UPDATE 的 LINQ API，這裡改用 Raw SQL（CLAUDE.md 允許的例外情況）。
        // 一次查詢鎖定所有列，不逐筆迴圈；鎖定順序由資料庫的 ORDER BY 保證，不靠 .NET 端排序，
        // 確保所有交易走同一條鎖定順序、避免死鎖——一筆訂單可能同時涉及多個不同的計數票種（design.md 決策 3）。
        return await _dbContext.TicketTypes
            .FromSqlInterpolated($"""
                SELECT * FROM "TicketTypes"
                WHERE "Id" = ANY({distinctIds})
                ORDER BY "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
    }
}
