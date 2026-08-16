namespace ProjectC.Application.Events.CreateEvent;

public sealed record CreateEventRequest(string Title, DateTime StartAtUtc, Guid VenueId, Guid SeatMapId);
