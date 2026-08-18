namespace ProjectC.Application.Events.GetAdminEvents;

public sealed record AdminEventSummaryDto(
    Guid Id,
    string Title,
    DateTime StartAtUtc,
    Guid VenueId,
    Guid SeatMapId,
    string? Description,
    string? PosterUrl,
    int? MaxTicketsPerOrder,
    Guid? CreatedByMemberId,
    string? CreatedByDisplayName,
    DateTime? CreatedAtUtc,
    int AvailableSeatCount,
    int HeldSeatCount,
    int SoldSeatCount);
