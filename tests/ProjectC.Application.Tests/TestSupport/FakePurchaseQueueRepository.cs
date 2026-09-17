using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakePurchaseQueueRepository : IPurchaseQueueRepository
{
    public List<PurchaseQueueEntry> Data { get; } = new();

    public Task<PurchaseQueueEntry?> GetCurrentAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
        => Task.FromResult(Data
            .Where(e => e.EventId == eventId && e.MemberId == memberId &&
                (e.Status == PurchaseQueueEntryStatus.Waiting
                    || e.Status == PurchaseQueueEntryStatus.Admitted
                    || e.Status == PurchaseQueueEntryStatus.Expired))
            .OrderByDescending(e => e.JoinedAtUtc)
            .ThenByDescending(e => e.Id)
            .FirstOrDefault());

    public Task<PurchaseQueueEntry?> GetForUpdateAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(e => e.EventId == eventId && e.MemberId == memberId &&
            (e.Status == PurchaseQueueEntryStatus.Waiting || e.Status == PurchaseQueueEntryStatus.Admitted)));

    public Task<IReadOnlyList<PurchaseQueueEntry>> GetActiveForReconciliationAsync(Guid eventId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PurchaseQueueEntry>>(Data
            .Where(e => e.EventId == eventId && (e.Status == PurchaseQueueEntryStatus.Waiting || e.Status == PurchaseQueueEntryStatus.Admitted))
            .ToList());

    public Task<int> AdmitBatchAsync(IReadOnlyCollection<Guid> entryIds, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken)
    {
        var affected = 0;
        foreach (var entry in Data.Where(e => entryIds.Contains(e.Id) && e.Status == PurchaseQueueEntryStatus.Waiting))
        {
            entry.Admit(admittedAtUtc, admissionExpiresAtUtc);
            affected++;
        }

        return Task.FromResult(affected);
    }

    public Task<int> ExpireBatchAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken)
    {
        var affected = 0;
        foreach (var entry in Data.Where(e => entryIds.Contains(e.Id) && e.Status == PurchaseQueueEntryStatus.Admitted))
        {
            entry.Expire();
            affected++;
        }

        return Task.FromResult(affected);
    }

    public Task<int> AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken)
    {
        var entry = Data.FirstOrDefault(e => e.Id == entryId && e.Status == PurchaseQueueEntryStatus.Waiting);
        if (entry is null)
        {
            return Task.FromResult(0);
        }

        entry.Admit(admittedAtUtc, admissionExpiresAtUtc);
        return Task.FromResult(1);
    }

    public Task<int> CountWaitingAheadAsync(Guid eventId, DateTime joinedAtUtc, Guid entryId, CancellationToken cancellationToken)
        => Task.FromResult(Data.Count(e => e.EventId == eventId && e.Status == PurchaseQueueEntryStatus.Waiting &&
            (e.JoinedAtUtc < joinedAtUtc || (e.JoinedAtUtc == joinedAtUtc && e.Id < entryId))));

    // 比照真正的 ON CONFLICT DO NOTHING 語意，但單執行緒的 Fake 不需要真的處理併發衝突：
    // 撞到既有進行中紀錄時直接回傳該筆既有紀錄，否則新增並回傳。
    public Task<PurchaseQueueEntry> AddOrGetExistingAsync(PurchaseQueueEntry newEntry, CancellationToken cancellationToken)
    {
        var existing = Data.FirstOrDefault(e => e.EventId == newEntry.EventId && e.MemberId == newEntry.MemberId &&
            (e.Status == PurchaseQueueEntryStatus.Waiting || e.Status == PurchaseQueueEntryStatus.Admitted));
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        Data.Add(newEntry);
        return Task.FromResult(newEntry);
    }
}
