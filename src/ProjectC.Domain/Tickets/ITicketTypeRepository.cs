namespace ProjectC.Domain.Tickets;

public interface ITicketTypeRepository
{
    Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(TicketType ticketType);
}
