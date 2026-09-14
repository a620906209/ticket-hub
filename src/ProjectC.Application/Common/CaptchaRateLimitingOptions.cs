using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 獨立於 RateLimitingOptions／LoginRateLimitingOptions：驗證碼取碼頻率與登入頻率的防護目的不同
// （防止 token 農場 vs 防止密碼窮舉），額度與時間窗沒有理由綁在一起（captcha-verification design.md 決策 6）。
// 有安全的預設值（PermitLimit = 10、WindowSeconds = 60），缺漏時套用，不需要 ValidateOnStart；
// 設定但為 0 或負數時仍 MUST 被 DataAnnotations 擋下。
public sealed class CaptchaRateLimitingOptions
{
    public const string SectionName = "CaptchaRateLimiting";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
