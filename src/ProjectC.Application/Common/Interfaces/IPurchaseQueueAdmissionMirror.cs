namespace ProjectC.Application.Common.Interfaces;

// 放置於 Application/Common/Interfaces 而非 Domain：比照既有 IDistributedLock／ICaptchaService／
// IQueryCache 的既定放置慣例，這個介面同樣不承載業務語意（purchase-queue-redis-admission
// design.md Decision 3／8）。
/// <summary>
/// PQ-JOIN／PQ-COMPLETE 對 Redis 入場推進鏡像的 best-effort 同步（purchase-queue-redis-admission
/// design.md Decision 3／8）。兩個方法 MUST NOT 拋出例外——失敗不影響呼叫端（加入排隊／訂單完成）
/// 本身的成功與否，只記錄可觀察 log，收斂交由背景服務的鏡像校正機制負責。
/// </summary>
public interface IPurchaseQueueAdmissionMirror
{
    /// <summary>PQ-JOIN 交易 commit 後呼叫：ZADD 寫入 waiting zset（design.md Decision 3）。</summary>
    Task SyncJoinAsync(Guid eventId, Guid entryId, DateTime joinedAtUtc, CancellationToken cancellationToken);

    /// <summary>PQ-COMPLETE 交易 commit 後呼叫：ZREM 移除 admitted zset（design.md Decision 8）。</summary>
    Task SyncCompletionAsync(Guid eventId, Guid entryId, CancellationToken cancellationToken);
}
