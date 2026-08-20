namespace ProjectC.Domain.Tickets;

public interface ITicketSigningService
{
    /// <summary>依 Ticket ID 產生按需簽章內容，可隨時重新推導、不需要持久化（design.md 決策 3）。</summary>
    string Sign(Guid ticketId);

    /// <summary>驗證內容是否為本服務簽出、未被竄改；驗證通過時 <paramref name="ticketId"/> 還原為原始 Ticket ID。
    /// 比照 .NET Try-pattern 慣例（如 <see cref="Guid.TryParse(string?, out Guid)"/>），<paramref name="content"/>
    /// 為 <c>null</c>／空字串／任意格式不符的輸入 MUST 回傳 <c>false</c>，不得拋出例外。</summary>
    bool TryVerify(string? content, out Guid ticketId);
}
