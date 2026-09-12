using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Events.GetEvents;

public sealed class GetEventsHandler
{
    public const string CacheKey = "query-cache:events:list";

    private readonly IEventRepository _eventRepository;
    private readonly IQueryCache _queryCache;
    private readonly QueryCacheOptions _queryCacheOptions;

    public GetEventsHandler(IEventRepository eventRepository, IQueryCache queryCache, QueryCacheOptions queryCacheOptions)
    {
        _eventRepository = eventRepository;
        _queryCache = queryCache;
        _queryCacheOptions = queryCacheOptions;
    }

    public async Task<IReadOnlyList<EventDto>> HandleAsync(CancellationToken cancellationToken)
    {
        var cached = await _queryCache.GetAsync<IReadOnlyList<EventDto>>(CacheKey, cancellationToken);
        if (cached.IsHit)
        {
            return cached.Value!;
        }

        var events = await _eventRepository.GetAllAsync(cancellationToken);
        IReadOnlyList<EventDto> dtos = events
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

        await _queryCache.SetAsync(CacheKey, dtos, TimeSpan.FromSeconds(_queryCacheOptions.EventListTtlSeconds), cancellationToken);
        return dtos;
    }
}
