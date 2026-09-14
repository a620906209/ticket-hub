using FluentValidation;

namespace ProjectC.Application.Common.Validation;

// 三個受驗證碼保護的端點（登入、註冊、加入排隊）共用同一套規則，避免逐一重複同一段 RuleFor
// lambda（captcha-verification design.md 決策 8）。MUST 先 trim 再驗證長度，不能反過來：對未 trim
// 的原始字串做 MaximumLength 檢查，會讓「trim 後合法」的輸入被長度規則誤擋（design.md 決策 8 原文範例）。
// FluentValidation 12.1.1 已移除 .Transform()（design.md 決策 8 版本核對），MUST 改用在 RuleFor lambda
// 內直接轉換值的寫法，並用 .OverridePropertyName() 維持錯誤訊息對應正確欄位名稱。
// 此寫法只影響規則鏈內部用於驗證的值，不會反過來改寫呼叫端的 CaptchaToken／CaptchaAnswer 屬性本身。
public static class CaptchaValidationRules
{
    public static void ApplyCaptchaValidation<T>(
        this AbstractValidator<T> validator,
        Func<T, string> captchaTokenSelector,
        Func<T, string> captchaAnswerSelector)
    {
        validator.RuleFor(x => captchaTokenSelector(x) == null ? null : captchaTokenSelector(x).Trim())
            .NotEmpty()
            .MaximumLength(64)
            .OverridePropertyName("CaptchaToken");

        validator.RuleFor(x => captchaAnswerSelector(x) == null ? null : captchaAnswerSelector(x).Trim())
            .NotEmpty()
            .MaximumLength(16)
            .OverridePropertyName("CaptchaAnswer");
    }
}
