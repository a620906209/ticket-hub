using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Tickets;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class TicketTypeRepository : ITicketTypeRepository
{
    private readonly ApplicationDbContext _dbContext;

    public TicketTypeRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.TicketTypes.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
        => await _dbContext.TicketTypes.Where(t => t.EventId == eventId).ToListAsync(cancellationToken);

    public void Add(TicketType ticketType) => _dbContext.TicketTypes.Add(ticketType);
}
