using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeVenueRepository : IVenueRepository
{
    public List<Venue> Data { get; } = new();

    public Task<Venue?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Data.FirstOrDefault(v => v.Id == id));

    public void Add(Venue venue) => Data.Add(venue);
}
