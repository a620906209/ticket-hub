using ProjectC.Application.Common;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Venues.GetVenueById;

public sealed class GetVenueByIdHandler
{
    private readonly IVenueRepository _venueRepository;
    private readonly ISeatMapRepository _seatMapRepository;

    public GetVenueByIdHandler(IVenueRepository venueRepository, ISeatMapRepository seatMapRepository)
    {
        _venueRepository = venueRepository;
        _seatMapRepository = seatMapRepository;
    }

    public async Task<Result<VenueDetailDto>> HandleAsync(Guid venueId, CancellationToken cancellationToken)
    {
        var venue = await _venueRepository.GetByIdAsync(venueId, cancellationToken);
        if (venue is null)
        {
            return Result<VenueDetailDto>.Failure(Error.NotFound($"Venue '{venueId}' was not found."));
        }

        var seatMaps = await _seatMapRepository.GetByVenueIdAsync(venueId, cancellationToken);
        var seatMapSummaries = seatMaps.Select(m => new SeatMapSummaryDto(m.Id, m.Seats.Count)).ToList();

        return Result<VenueDetailDto>.Success(new VenueDetailDto(venue.Id, venue.Name, seatMapSummaries));
    }
}
