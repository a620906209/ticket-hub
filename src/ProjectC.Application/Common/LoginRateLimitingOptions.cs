using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 有安全的預設值（PermitLimit = 5、WindowSeconds = 60），缺漏時套用，不需要 ValidateOnStart；
// 設定但為 0 或負數時仍 MUST 被 DataAnnotations 擋下（login-rate-limiting design.md 決策 2）。
// 獨立於 RateLimitingOptions：分區鍵語意不同（來源 IP vs 已登入會員 Id），數值也刻意設得更嚴格
// （登入端點的正常呼叫頻率遠低於下單端點）。
public sealed class LoginRateLimitingOptions
{
    public const string SectionName = "LoginRateLimiting";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 5;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}
