namespace ProjectC.Application.Venues.CreateSeatMap;

public sealed record CreateSeatMapRequest(IReadOnlyList<SeatRequest> Seats);

public sealed record SeatRequest(string ZoneCode, string SeatNumber);
