namespace ProjectC.Domain.Venues;

public interface IVenueRepository
{
    Task<Venue?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Venue>> GetAllAsync(CancellationToken cancellationToken);

    void Add(Venue venue);
}
