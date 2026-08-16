using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeTicketTypeRepository : ITicketTypeRepository
{
    public List<TicketType> Data { get; } = new();

    public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(t => t.Id == id));

    public void Add(TicketType ticketType) => Data.Add(ticketType);
}
