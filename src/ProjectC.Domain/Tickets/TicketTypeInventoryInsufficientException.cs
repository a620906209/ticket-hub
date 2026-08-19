using ProjectC.Domain.Common;

namespace ProjectC.Domain.Tickets;

public sealed class TicketTypeInventoryInsufficientException : DomainException
{
    public Guid TicketTypeId { get; }
    public int Requested { get; }
    public int Available { get; }

    public TicketTypeInventoryInsufficientException(Guid ticketTypeId, int requested, int available)
        : base($"Ticket type '{ticketTypeId}' has {available} available but {requested} were requested.")
    {
        TicketTypeId = ticketTypeId;
        Requested = requested;
        Available = available;
    }
}
