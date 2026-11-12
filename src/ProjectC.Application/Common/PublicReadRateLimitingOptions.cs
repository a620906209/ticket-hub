using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 公開活動讀取端點（匿名、以來源 IP 分區）的獨立額度，不與 login／captcha 共用數值
// （reverse-proxy-forwarded-headers design.md 決策 6）。有安全的預設值（PermitLimit = 120、WindowSeconds = 60），
// 缺漏時套用，不需要 ValidateOnStart；設定但為 0 或負數時仍 MUST 被 DataAnnotations 擋下。
public sealed class PublicReadRateLimitingOptions
{
    public const string SectionName = "PublicReadRateLimiting";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 120;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
