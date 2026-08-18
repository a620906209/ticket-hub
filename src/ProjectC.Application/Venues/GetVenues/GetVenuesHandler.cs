using ProjectC.Domain.Venues;

namespace ProjectC.Application.Venues.GetVenues;

public sealed class GetVenuesHandler
{
    private readonly IVenueRepository _venueRepository;

    public GetVenuesHandler(IVenueRepository venueRepository)
    {
        _venueRepository = venueRepository;
    }

    public async Task<IReadOnlyList<VenueSummaryDto>> HandleAsync(CancellationToken cancellationToken)
    {
        var venues = await _venueRepository.GetAllAsync(cancellationToken);

        return venues
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ThenBy(v => v.Id)
            .Select(v => new VenueSummaryDto(v.Id, v.Name))
            .ToList();
    }
}
