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

    public void Add(SeatMap seatMap) => _dbContext.SeatMaps.Add(seatMap);
}
