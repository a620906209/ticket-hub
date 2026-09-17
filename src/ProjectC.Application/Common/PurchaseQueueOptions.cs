using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 三者皆走 fail-fast 驗證，不當作「有安全預設值」的設定：刻意不給 C# 層級預設值，缺漏時維持 0，
// 會被下方 [Range] 擋下、與「設定但為 0 或負數」得到同樣的 fail-fast 效果（rate-limiting-queue design.md 決策 3）。
public sealed class PurchaseQueueOptions
{
    public const string SectionName = "PurchaseQueue";

    [Range(1, int.MaxValue)]
    public int MaxConcurrentAdmittedBuyers { get; set; }

    [Range(1, int.MaxValue)]
    public int AdmissionTtlSeconds { get; set; }

    [Range(1, int.MaxValue)]
    public int PollingIntervalSeconds { get; set; }

    // Decision 9 正式契約值：pending 標記 TTL，預設 30 秒，有安全預設值不需要額外 fail-fast
    // （purchase-queue-redis-admission design.md 決策 9）。
    [Range(1, int.MaxValue)]
    public int AdmissionPendingTtlSeconds { get; set; } = 30;

    // Decision 8「Log 等級升級門檻」：同一 entryId 連續失敗達此輪數時，當次失敗 log 升級為 Error
    // （purchase-queue-redis-admission design.md 決策 8）。
    [Range(1, int.MaxValue)]
    public int ReconciliationFailureLogUpgradeThreshold { get; set; } = 10;
}
