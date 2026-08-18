using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Venues;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class VenueRepository : IVenueRepository
{
    private readonly ApplicationDbContext _dbContext;

    public VenueRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Venue?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.Venues.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Venue>> GetAllAsync(CancellationToken cancellationToken)
        => await _dbContext.Venues.ToListAsync(cancellationToken);

    public void Add(Venue venue) => _dbContext.Venues.Add(venue);
}
