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
    /// 依 EventId 篩選 Status IN (Waiting, Admitted) 的 no-tracking 快照查詢，供 Redis 鏡像校正機制
    /// 使用（design.md Decision 5 步驟 1）；不加鎖——校正機制的正確性建立在冪等的差集比對與條件式
    /// UPDATE 上，不需要悲觀鎖（見 purchase-queue-redis-admission design.md Decision 5）。
    /// </summary>
    Task<IReadOnlyList<PurchaseQueueEntry>> GetActiveForReconciliationAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>
    /// 批次條件式 UPDATE：僅 Status = Waiting 的紀錄轉為 Admitted，回傳整批總影響列數（不保證能反推
    /// 哪幾筆生效，見 design.md Decision 4「批次執行與逐筆結果的落差」）。供入場推進 Lua Script 決策
    /// 結果落地使用（design.md Decision 4）。
    /// </summary>
    Task<int> AdmitBatchAsync(IReadOnlyCollection<Guid> entryIds, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// 批次條件式 UPDATE：僅 Status = Admitted 的紀錄轉為 Expired，回傳整批總影響列數（同上，不保證
    /// 逐筆結果）。供入場推進 Lua Script 逾時清除結果落地、及 Decision 5 步驟 8 放棄的逾時標記修復共用
    /// （design.md Decision 4／5）。
    /// </summary>
    Task<int> ExpireBatchAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken);

    /// <summary>
    /// 單筆條件式 UPDATE：僅 Status = Waiting 的紀錄轉為 Admitted，回傳影響列數（0 或 1）。供 Decision 5
    /// 步驟 7「偵測並修復放棄的推進決策」使用，每筆的 AdmissionExpiresAtUtc 取自 Redis admitted zset
    /// 該成員各自的 score，無法比照 AdmitBatchAsync 共用單一值批次處理（design.md Decision 5）。
    /// </summary>
    Task<int> AdmitIfWaitingAsync(Guid entryId, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// 計算依 JoinedAtUtc ASC, Id ASC 排序下，早於指定紀錄的 Waiting 筆數，供排隊狀態查詢（PQ-STATUS）
    /// 的「前方等待人數」使用；此排序規則維持純 Postgres 計算，與入場推進機制（PQ-ADMIT，見
    /// purchase-queue-redis-admission design.md Decision 2）改用 Redis waiting zset 的 member 字串
    /// lexicographic 順序做同毫秒 tie-break 是各自獨立的排序契約，極少數同毫秒情況下兩者可能有微小
    /// 數字差異，此為已知、可接受的限制，PQ-STATUS 不需要與 PQ-ADMIT 的排序完全一致。
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
