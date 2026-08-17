namespace ProjectC.Application.Events.GetEventSeats;

public sealed record EventSeatDto(Guid EventSeatId, string ZoneCode, string SeatNumber, string Status);
