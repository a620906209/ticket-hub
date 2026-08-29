namespace ProjectC.Application.Events.GetEvents;

public sealed record EventDto(
    Guid Id,
    string Title,
    DateTime StartAtUtc,
    Guid VenueId,
    Guid SeatMapId,
    string? Description,
    string? PosterUrl,
    int? MaxTicketsPerOrder,
    bool IsQueueModeEnabled);
