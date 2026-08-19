using ProjectC.Domain.Common;

namespace ProjectC.Domain.Tickets;

/// <summary>防禦性資料完整性檢查：正常建構的 TicketType 必然滿足 RequiresSeat = false ⟺ AvailableQuantity 有值，
/// 這個例外只會在該不變量被破壞時（例如損毀資料）觸發，不是正常業務路徑。</summary>
public sealed class TicketTypeInventoryNotConfiguredException : DomainException
{
    public Guid TicketTypeId { get; }

    public TicketTypeInventoryNotConfiguredException(Guid ticketTypeId)
        : base($"Ticket type '{ticketTypeId}' does not require a seat but has no configured available quantity.")
    {
        TicketTypeId = ticketTypeId;
    }
}
