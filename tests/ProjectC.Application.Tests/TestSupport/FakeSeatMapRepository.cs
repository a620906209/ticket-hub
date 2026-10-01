using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeSeatMapRepository : ISeatMapRepository
{
    public List<SeatMap> Data { get; } = new();

    public int GetByIdCallCount { get; private set; }
    public CancellationToken? LastGetByIdToken { get; private set; }
    public int GetSeatsByIdsCallCount { get; private set; }
    public IReadOnlyList<Guid>? LastGetSeatsByIdsIds { get; private set; }
    public CancellationToken? LastGetSeatsByIdsToken { get; private set; }

    public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        GetByIdCallCount++;
        LastGetByIdToken = cancellationToken;
        return Task.FromResult(Data.FirstOrDefault(m => m.Id == id));
    }

    public Task<IReadOnlyList<SeatMap>> GetByVenueIdAsync(Guid venueId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SeatMap>>(Data.Where(m => m.VenueId == venueId).ToList());

    public Task<IReadOnlyList<Seat>> GetSeatsByIdsAsync(IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken)
    {
        GetSeatsByIdsCallCount++;
        LastGetSeatsByIdsIds = seatIds.ToList();
        LastGetSeatsByIdsToken = cancellationToken;
        return Task.FromResult<IReadOnlyList<Seat>>(Data.SelectMany(m => m.Seats).Where(s => seatIds.Contains(s.Id)).ToList());
    }

    public void Add(SeatMap seatMap) => Data.Add(seatMap);
}
