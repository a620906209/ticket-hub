using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class PurchaseQueueRepository : IPurchaseQueueRepository
{
    private readonly ApplicationDbContext _dbContext;

    public PurchaseQueueRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<PurchaseQueueEntry?> GetCurrentAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
        => _dbContext.PurchaseQueueEntries
            .AsNoTracking()
            .Where(e => e.EventId == eventId && e.MemberId == memberId &&
                (e.Status == PurchaseQueueEntryStatus.Waiting
                    || e.Status == PurchaseQueueEntryStatus.Admitted
                    || e.Status == PurchaseQueueEntryStatus.Expired))
            .OrderByDescending(e => e.JoinedAtUtc)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // EF Core 沒有對應 SELECT ... FOR UPDATE 的 LINQ API，改用 Raw SQL（CLAUDE.md 允許的例外情況）。
    public async Task<PurchaseQueueEntry?> GetForUpdateAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
    {
        _dbContext.EnsureActiveTransaction(nameof(GetForUpdateAsync));

        return await _dbContext.PurchaseQueueEntries
            .FromSqlInterpolated($"""
                SELECT * FROM "PurchaseQueueEntries"
                WHERE "EventId" = {eventId} AND "MemberId" = {memberId}
                    AND "Status" IN ('Waiting', 'Admitted')
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PurchaseQueueEntry>> GetForAdmissionAsync(Guid eventId, CancellationToken cancellationToken)
    {
        _dbContext.EnsureActiveTransaction(nameof(GetForAdmissionAsync));

        return await _dbContext.PurchaseQueueEntries
            .FromSqlInterpolated($"""
                SELECT * FROM "PurchaseQueueEntries"
                WHERE "EventId" = {eventId} AND "Status" IN ('Waiting', 'Admitted')
                ORDER BY "JoinedAtUtc" ASC, "Id" ASC
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountWaitingAheadAsync(Guid eventId, DateTime joinedAtUtc, Guid entryId, CancellationToken cancellationToken)
        => _dbContext.PurchaseQueueEntries
            .AsNoTracking()
            .Where(e => e.EventId == eventId && e.Status == PurchaseQueueEntryStatus.Waiting &&
                (e.JoinedAtUtc < joinedAtUtc || (e.JoinedAtUtc == joinedAtUtc && e.Id < entryId)))
            .CountAsync(cancellationToken);

    public async Task<PurchaseQueueEntry> AddOrGetExistingAsync(PurchaseQueueEntry newEntry, CancellationToken cancellationToken)
    {
        // 防禦性檢查（審查後新增）：這個方法依賴呼叫端與任何先前的 Expire() 等變更位於同一交易內，
        // 才能保證上面的 SaveChangesAsync flush 有意義；沒有進行中的交易時貿然執行還是會插入成功，
        // 但會失去「與其他寫入同一次 Commit／Rollback」的原子性保證，比照 GetForUpdateAsync／
        // GetForAdmissionAsync 的既定慣例 fail fast，避免未來呼叫端忘記先開交易而不自知。
        _dbContext.EnsureActiveTransaction(nameof(AddOrGetExistingAsync));

        // 先落地本交易內任何尚未寫入的 ChangeTracker 變更（例如呼叫端在同一交易內剛對某筆逾時紀錄呼叫過
        // Expire()）。下面的 INSERT 是繞過 ChangeTracker 的 raw SQL，不會自動看到未落地的變更；若不先
        // flush，資料庫裡舊紀錄仍是 Admitted，partial unique index 會讓 ON CONFLICT DO NOTHING 誤判新
        // 紀錄撞到「進行中」紀錄而略過插入。無待寫入變更時 SaveChangesAsync 是no-op，不影響一般新增流程。
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (await TryInsertAsync(newEntry, cancellationToken))
        {
            return newEntry;
        }

        var existing = await GetCurrentInProgressAsync(newEntry.EventId, newEntry.MemberId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // 極端情況（理論上罕見）：MUST NOT 無限重試，僅重試一次（design.md 決策 3）。
        if (await TryInsertAsync(newEntry, cancellationToken))
        {
            return newEntry;
        }

        existing = await GetCurrentInProgressAsync(newEntry.EventId, newEntry.MemberId, cancellationToken);
        return existing ?? throw new PurchaseQueueJoinConflictException(newEntry.EventId, newEntry.MemberId);
    }

    // 完全繞過 EF Core change tracking：不呼叫 DbSet.Add(...)，entry 從頭到尾不進入 ChangeTracker，
    // 撞到既有進行中紀錄時 ON CONFLICT DO NOTHING 讓這是不拋例外的正常結果（design.md 決策 3）。
    private async Task<bool> TryInsertAsync(PurchaseQueueEntry entry, CancellationToken cancellationToken)
    {
        var affected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PurchaseQueueEntries"
                ("Id", "EventId", "MemberId", "Status", "JoinedAtUtc", "AdmittedAtUtc", "AdmissionExpiresAtUtc")
            VALUES
                ({entry.Id}, {entry.EventId}, {entry.MemberId}, {entry.Status.ToString()}, {entry.JoinedAtUtc}, {entry.AdmittedAtUtc}, {entry.AdmissionExpiresAtUtc})
            ON CONFLICT ("EventId", "MemberId") WHERE "Status" IN ('Waiting', 'Admitted') DO NOTHING
            """, cancellationToken);

        return affected == 1;
    }

    // 不加鎖：呼叫時機是 ON CONFLICT DO NOTHING 判定衝突之後，PostgreSQL 的等待/重新檢查語意保證
    // 讓本次插入判定為衝突的那筆紀錄此時已提交，一般查詢即可讀到（design.md 決策 3）。
    private Task<PurchaseQueueEntry?> GetCurrentInProgressAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
        => _dbContext.PurchaseQueueEntries
            .AsNoTracking()
            .Where(e => e.EventId == eventId && e.MemberId == memberId &&
                (e.Status == PurchaseQueueEntryStatus.Waiting || e.Status == PurchaseQueueEntryStatus.Admitted))
            .SingleOrDefaultAsync(cancellationToken);
}
