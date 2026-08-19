using ProjectC.Domain.Common;

namespace ProjectC.Domain.Tickets;

public sealed class TicketTypeRequiresSeatException : DomainException
{
    public Guid TicketTypeId { get; }

    public TicketTypeRequiresSeatException(Guid ticketTypeId)
        : base($"Ticket type '{ticketTypeId}' requires a seat and does not support quantity-based reservation.")
    {
        TicketTypeId = ticketTypeId;
    }
}
