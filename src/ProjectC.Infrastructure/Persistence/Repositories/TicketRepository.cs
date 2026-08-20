using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Tickets;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class TicketRepository : ITicketRepository
{
    private readonly ApplicationDbContext _dbContext;

    public TicketRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Add(Ticket ticket) => _dbContext.Tickets.Add(ticket);

    public async Task<Ticket?> GetForUpdateAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        // PostgreSQL 的列鎖只在交易存續期間有效；沒有進行中的交易時，SELECT ... FOR UPDATE
        // 一返回鎖就釋放了，等於完全沒有鎖定保護，所以在這裡 fail fast（比照 EventSeatRepository/TicketTypeRepository）。
        if (_dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"{nameof(GetForUpdateAsync)} must be called within an active transaction " +
                "(see IUnitOfWork.BeginTransactionAsync); otherwise the row lock is released " +
                "as soon as this query returns.");
        }

        // EF Core 沒有對應 SELECT ... FOR UPDATE 的 LINQ API，這裡改用 Raw SQL（CLAUDE.md 允許的例外情況）。
        // 單筆查詢本身已同時完成鎖定與讀取最新狀態，不需要額外的 ReloadAsync 步驟。
        return await _dbContext.Tickets
            .FromSqlInterpolated($"""
                SELECT * FROM "Tickets"
                WHERE "Id" = {ticketId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
