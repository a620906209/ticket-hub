using ProjectC.Application.Common;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Venues.GetSeatMapById;

public sealed class GetSeatMapByIdHandler
{
    private readonly ISeatMapRepository _seatMapRepository;

    public GetSeatMapByIdHandler(ISeatMapRepository seatMapRepository)
    {
        _seatMapRepository = seatMapRepository;
    }

    public async Task<Result<SeatMapDetailDto>> HandleAsync(Guid venueId, Guid seatMapId, CancellationToken cancellationToken)
    {
        var seatMap = await _seatMapRepository.GetByIdAsync(seatMapId, cancellationToken);
        if (seatMap is null || seatMap.VenueId != venueId)
        {
            return Result<SeatMapDetailDto>.Failure(Error.NotFound($"Seat map '{seatMapId}' was not found."));
        }

        var seats = seatMap.Seats.Select(s => new SeatDto(s.Id, s.ZoneCode, s.SeatNumber)).ToList();

        return Result<SeatMapDetailDto>.Success(new SeatMapDetailDto(seatMap.Id, seatMap.VenueId, seats));
    }
}
