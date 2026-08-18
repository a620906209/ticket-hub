using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeSeatMapRepository : ISeatMapRepository
{
    public List<SeatMap> Data { get; } = new();

    public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(m => m.Id == id));

    public Task<IReadOnlyList<SeatMap>> GetByVenueIdAsync(Guid venueId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SeatMap>>(Data.Where(m => m.VenueId == venueId).ToList());

    public void Add(SeatMap seatMap) => Data.Add(seatMap);
}
