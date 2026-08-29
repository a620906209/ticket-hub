using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 有安全的預設值（PermitLimit = 20、WindowSeconds = 60），缺漏時套用，不需要 ValidateOnStart；
// 設定但為 0 或負數時仍 MUST 被 DataAnnotations 擋下（rate-limiting-queue design.md 決策 1）。
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 20;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
