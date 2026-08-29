using ProjectC.Domain.Events;

namespace ProjectC.Application.Events.GetEvents;

public sealed class GetEventsHandler
{
    private readonly IEventRepository _eventRepository;

    public GetEventsHandler(IEventRepository eventRepository)
    {
        _eventRepository = eventRepository;
    }

    public async Task<IReadOnlyList<EventDto>> HandleAsync(CancellationToken cancellationToken)
    {
        var events = await _eventRepository.GetAllAsync(cancellationToken);
        return events
            .Select(e => new EventDto(
                e.Id,
                e.Title,
                e.StartAtUtc,
                e.VenueId,
                e.SeatMapId,
                e.Description,
                e.PosterUrl,
                e.MaxTicketsPerOrder,
                e.IsQueueModeEnabled))
            .ToList();
    }
}
