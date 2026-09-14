using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// 有安全的預設值（TtlSeconds = 120），缺漏時套用，不需要 ValidateOnStart；設定但為 0 或負數時
// 仍 MUST 被 DataAnnotations 擋下——比照 CaptchaRateLimitingOptions／LoginRateLimitingOptions 的
// 既定模式，不是 QueryCacheOptions（無預設值，缺漏即 fail-fast）（captcha-verification design.md 決策 14）。
// 驗證碼固定為 4 碼英數字（design.md 決策 2），MUST NOT 提供可設定的 Length。
public sealed class CaptchaOptions
{
    public const string SectionName = "Captcha";

    [Range(1, int.MaxValue)]
    public int TtlSeconds { get; set; } = 120;
}
