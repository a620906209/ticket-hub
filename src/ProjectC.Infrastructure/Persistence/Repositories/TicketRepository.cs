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
        _dbContext.EnsureActiveTransaction(nameof(GetForUpdateAsync));

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
