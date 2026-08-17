using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeTicketTypeRepository : ITicketTypeRepository
{
    public List<TicketType> Data { get; } = new();

    public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(t => t.Id == id));

    public Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TicketType>>(Data.Where(t => t.EventId == eventId).ToList());

    public void Add(TicketType ticketType) => Data.Add(ticketType);
}
