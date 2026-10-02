namespace ProjectC.Application.Tickets.GetTicketHolder;

/// <summary>完整末四碼只在此 DTO 出現，供現場人員比對證件（real-name-verification design.md 決策 4、5）。</summary>
public sealed record TicketHolderDto(
    Guid TicketId,
    string TicketStatus,
    bool IsRealNameRequired,
    string? HolderRealName,
    string? HolderNationalIdLast4);
