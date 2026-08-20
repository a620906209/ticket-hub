using ProjectC.Domain.Common;

namespace ProjectC.Domain.Tickets;

public sealed class TicketNotIssuedException : DomainException
{
    public Guid TicketId { get; }
    public TicketStatus Status { get; }

    public TicketNotIssuedException(Guid ticketId, TicketStatus status)
        : base($"Ticket '{ticketId}' requires status Issued but is currently '{status}'.")
    {
        TicketId = ticketId;
        Status = status;
    }
}
