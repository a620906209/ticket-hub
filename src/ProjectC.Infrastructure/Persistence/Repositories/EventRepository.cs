using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Events;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class EventRepository : IEventRepository
{
    private readonly ApplicationDbContext _dbContext;

    public EventRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.Events.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken)
        => await _dbContext.Events.ToListAsync(cancellationToken);

    public void Add(Event @event) => _dbContext.Events.Add(@event);
}
