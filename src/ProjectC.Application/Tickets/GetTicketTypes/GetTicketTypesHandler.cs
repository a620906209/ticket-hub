using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tickets.GetTicketTypes;

public sealed class GetTicketTypesHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly ITicketTypeRepository _ticketTypeRepository;
    private readonly IQueryCache _queryCache;
    private readonly QueryCacheOptions _queryCacheOptions;

    public GetTicketTypesHandler(
        IEventRepository eventRepository,
        ITicketTypeRepository ticketTypeRepository,
        IQueryCache queryCache,
        QueryCacheOptions queryCacheOptions)
    {
        _eventRepository = eventRepository;
        _ticketTypeRepository = ticketTypeRepository;
        _queryCache = queryCache;
        _queryCacheOptions = queryCacheOptions;
    }

    public static string BuildCacheKey(Guid eventId) => $"query-cache:ticket-types:event:{eventId}";

    public async Task<Result<IReadOnlyList<TicketTypeDto>>> HandleAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var @event = await _eventRepository.GetByIdAsync(eventId, cancellationToken);
        if (@event is null)
        {
            return Result<IReadOnlyList<TicketTypeDto>>.Failure(Error.NotFound($"Event '{eventId}' was not found."));
        }

        var cacheKey = BuildCacheKey(eventId);
        var cached = await _queryCache.GetAsync<IReadOnlyList<TicketTypeDto>>(cacheKey, cancellationToken);
        if (cached.IsHit)
        {
            return Result<IReadOnlyList<TicketTypeDto>>.Success(cached.Value!);
        }

        var ticketTypes = await _ticketTypeRepository.GetByEventIdAsync(eventId, cancellationToken);
        IReadOnlyList<TicketTypeDto> dtos = ticketTypes
            .Select(t => new TicketTypeDto(t.Id, t.ZoneCode, t.Price, t.RequiresSeat, t.AvailableQuantity))
            .ToList();

        await _queryCache.SetAsync(cacheKey, dtos, TimeSpan.FromSeconds(_queryCacheOptions.TicketTypesTtlSeconds), cancellationToken);
        return Result<IReadOnlyList<TicketTypeDto>>.Success(dtos);
    }
}
