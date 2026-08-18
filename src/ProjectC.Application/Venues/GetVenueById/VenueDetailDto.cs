namespace ProjectC.Application.Venues.GetVenueById;

public sealed record VenueDetailDto(Guid Id, string Name, IReadOnlyList<SeatMapSummaryDto> SeatMaps);
