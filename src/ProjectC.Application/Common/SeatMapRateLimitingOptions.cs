using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 建立座位圖端點放寬到 10MB 本文後，以 Organizer 分區限制同一 Organizer 反覆送大本文的頻率
// （request-body-size-limits design.md 決策 8）。比照 CaptchaRateLimitingOptions：有安全的預設值
// （PermitLimit = 10、WindowSeconds = 60），缺漏時套用，不需要 ValidateOnStart；設定但為 0 或負數時
// 仍 MUST 被 DataAnnotations 擋下。
public sealed class SeatMapRateLimitingOptions
{
    public const string SectionName = "SeatMapRateLimiting";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
