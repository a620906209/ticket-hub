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
}
