using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeSeatMapRepository : ISeatMapRepository
{
    public List<SeatMap> Data { get; } = new();

    public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(m => m.Id == id));

    public void Add(SeatMap seatMap) => Data.Add(seatMap);
}
