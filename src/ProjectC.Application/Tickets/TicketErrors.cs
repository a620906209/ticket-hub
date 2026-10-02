using ProjectC.Application.Common;

namespace ProjectC.Application.Tickets;

/// <summary>核銷與查詢持票人共用同一個 NotFound 訊息來源：兩個端點對「不存在」與「不屬於呼叫端 Organizer」
/// 必須回逐字相同的 body，否則可藉由比對差異推測票券存在性（RDM-AUTHZ-004、RDM-HOLDER-003）。</summary>
public static class TicketErrors
{
    public static Error NotFound(Guid ticketId) => Error.NotFound($"Ticket '{ticketId}' was not found.");
}
