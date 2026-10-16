using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeVenueRepository : IVenueRepository
{
    public List<Venue> Data { get; } = new();

    public int GetByIdCallCount { get; private set; }

    public Task<Venue?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        GetByIdCallCount++;
        return Task.FromResult(Data.FirstOrDefault(v => v.Id == id));
    }

    public Task<IReadOnlyList<Venue>> GetAllAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Venue>>(Data.ToList());

    public void Add(Venue venue) => Data.Add(venue);
}
