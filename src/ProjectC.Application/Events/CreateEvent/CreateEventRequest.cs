namespace ProjectC.Application.Events.CreateEvent;

public sealed record CreateEventRequest(
    string Title,
    DateTime StartAtUtc,
    Guid VenueId,
    Guid SeatMapId,
    string? Description = null,
    string? PosterUrl = null,
    int? MaxTicketsPerOrder = null,
    bool? IsRealNameRequired = null,
    DateTime? SalesStartAtUtc = null,
    DateTime? SalesEndAtUtc = null);
