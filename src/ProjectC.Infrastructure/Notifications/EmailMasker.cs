namespace ProjectC.Infrastructure.Notifications;

/// <summary>
/// 個資（Email）的 log 遮蔽 helper（見 email-notification design.md 決策 5）。合法定義：剛好包含一個
/// <c>@</c>，且 <c>@</c> 前後兩段各自 trim 前後 whitespace 後都至少包含一個非 whitespace 字元。
/// 不合法輸入一律回傳固定字串，MUST NOT 對任何輸入拋出例外——遮蔽是記錄通知失敗這個 best-effort
/// 動作的一部分，不能自己變成新的失敗來源。
/// </summary>
public static class EmailMasker
{
    private const string Redacted = "[redacted]";

    public static string Mask(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Redacted;
        }

        var atIndex = email.IndexOf('@');
        if (atIndex < 0 || email.IndexOf('@', atIndex + 1) >= 0)
        {
            return Redacted;
        }

        var localPart = email[..atIndex];
        var domainPart = email[(atIndex + 1)..];
        if (string.IsNullOrWhiteSpace(localPart) || string.IsNullOrWhiteSpace(domainPart))
        {
            return Redacted;
        }

        return $"{localPart[0]}***@{domainPart}";
    }
}
