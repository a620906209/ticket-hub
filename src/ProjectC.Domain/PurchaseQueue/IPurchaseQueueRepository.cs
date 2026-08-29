namespace ProjectC.Domain.PurchaseQueue;

public interface IPurchaseQueueRepository
{
    /// <summary>
    /// 依 EventId + MemberId 查詢「目前紀錄」（Status IN (Waiting, Admitted, Expired) 範圍內
    /// JoinedAtUtc DESC, Id DESC 取一筆），供查詢端點使用，不加鎖（見 design.md 決策 3「目前紀錄」選取規則）。
    /// </summary>
    Task<PurchaseQueueEntry?> GetCurrentAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>
    /// 依 EventId + MemberId 取得「進行中」（Status IN (Waiting, Admitted)）紀錄的悲觀鎖查詢，
    /// 供加入排隊流程與 OrderService.PlaceOrderAsync 重新確認排隊資格用（見 design.md 決策 3／4）。
    /// </summary>
    Task<PurchaseQueueEntry?> GetForUpdateAsync(Guid eventId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>
    /// 依 EventId 篩選 Status IN (Waiting, Admitted) 的悲觀鎖批次查詢，依 JoinedAtUtc ASC, Id ASC 排序，
    /// 供背景推進服務用，不含 Completed／Expired 歷史資料（見 design.md 決策 3）。
    /// </summary>
    Task<IReadOnlyList<PurchaseQueueEntry>> GetForAdmissionAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>
    /// 計算依 JoinedAtUtc ASC, Id ASC 排序下，早於指定紀錄的 Waiting 筆數，供排隊狀態查詢的「前方等待人數」
    /// 使用；排序規則須與入場推進機制（GetForAdmissionAsync）完全一致（見 design.md 決策 3）。
    /// </summary>
    Task<int> CountWaitingAheadAsync(Guid eventId, DateTime joinedAtUtc, Guid entryId, CancellationToken cancellationToken);

    /// <summary>
    /// 嘗試新增一筆排隊紀錄，若撞到 (EventId, MemberId) partial unique index（同一會員已有進行中紀錄）
    /// 則回傳該筆既有紀錄，否則回傳新增的紀錄。內部 MUST 用 INSERT ... ON CONFLICT ... DO NOTHING，
    /// 不得用「先 Add()＋SaveChangesAsync()、捕捉 DbUpdateException」的寫法（見 design.md 決策 3）。
    /// 呼叫前若已在同一交易內對其他紀錄（例如剛 Expire() 的既有紀錄）做過尚未落地的變更，實作 MUST
    /// 先將其落地寫入，才能執行這段繞過 ChangeTracker 的 raw SQL INSERT，否則資料庫看到的仍是舊狀態。
    /// </summary>
    Task<PurchaseQueueEntry> AddOrGetExistingAsync(PurchaseQueueEntry newEntry, CancellationToken cancellationToken);
}
