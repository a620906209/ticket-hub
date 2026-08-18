using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Venues;

namespace ProjectC.Infrastructure.Persistence.Repositories;

public class SeatMapRepository : ISeatMapRepository
{
    private readonly ApplicationDbContext _dbContext;

    public SeatMapRepository(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => _dbContext.SeatMaps.Include(m => m.Seats).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SeatMap>> GetByVenueIdAsync(Guid venueId, CancellationToken cancellationToken)
        => await _dbContext.SeatMaps.Include(m => m.Seats).Where(m => m.VenueId == venueId).ToListAsync(cancellationToken);

    public void Add(SeatMap seatMap) => _dbContext.SeatMaps.Add(seatMap);
}
