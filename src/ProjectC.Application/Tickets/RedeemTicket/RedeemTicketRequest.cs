namespace ProjectC.Application.Tickets.RedeemTicket;

/// <param name="IsHolderVerified">需實名活動時操作人員確認已比對持票人證件；未提供視為 false（real-name-verification design.md 決策 4）。</param>
public sealed record RedeemTicketRequest(string? Signature, bool? IsHolderVerified = null);
