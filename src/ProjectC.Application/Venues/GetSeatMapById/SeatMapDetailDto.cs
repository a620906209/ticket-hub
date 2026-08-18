namespace ProjectC.Application.Venues.GetSeatMapById;

public sealed record SeatMapDetailDto(Guid Id, Guid VenueId, IReadOnlyList<SeatDto> Seats);
