using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Events.GetAdminEvents;

public sealed class GetAdminEventsHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly IApplicationDbContext _dbContext;
    private readonly IEventSeatRepository _eventSeatRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetAdminEventsHandler(
        IEventRepository eventRepository,
        IApplicationDbContext dbContext,
        IEventSeatRepository eventSeatRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _eventRepository = eventRepository;
        _dbContext = dbContext;
        _eventSeatRepository = eventSeatRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<IReadOnlyList<AdminEventSummaryDto>> HandleAsync(CancellationToken cancellationToken)
    {
        var events = await _eventRepository.GetAllAsync(cancellationToken);

        var memberIds = events
            .Where(e => e.CreatedByMemberId is not null)
            .Select(e => e.CreatedByMemberId!.Value)
            .Distinct()
            .ToList();
        var displayNamesByMemberId = await _dbContext.Members
            .Where(m => memberIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.DisplayName, cancellationToken);

        var eventIds = events.Select(e => e.Id).ToList();
        var seats = await _eventSeatRepository.GetByEventIdsAsync(eventIds, cancellationToken);
        var now = _dateTimeProvider.UtcNow;
        var seatsByEventId = seats.ToLookup(s => s.EventId);

        return events
            .Select(e =>
            {
                var eventSeats = seatsByEventId[e.Id];
                var availableCount = 0;
                var heldCount = 0;
                var soldCount = 0;
                foreach (var seat in eventSeats)
                {
                    switch (seat.GetStatus(now))
                    {
                        case EventSeatStatus.Available:
                            availableCount++;
                            break;
                        case EventSeatStatus.Held:
                            heldCount++;
                            break;
                        case EventSeatStatus.Sold:
                            soldCount++;
                            break;
                    }
                }

                var createdByDisplayName = e.CreatedByMemberId is { } memberId && displayNamesByMemberId.TryGetValue(memberId, out var displayName)
                    ? displayName
                    : null;

                return new AdminEventSummaryDto(
                    e.Id,
                    e.Title,
                    e.StartAtUtc,
                    e.VenueId,
                    e.SeatMapId,
                    e.Description,
                    e.PosterUrl,
                    e.MaxTicketsPerOrder,
                    e.CreatedByMemberId,
                    createdByDisplayName,
                    e.CreatedAtUtc,
                    availableCount,
                    heldCount,
                    soldCount);
            })
            .ToList();
    }
}
