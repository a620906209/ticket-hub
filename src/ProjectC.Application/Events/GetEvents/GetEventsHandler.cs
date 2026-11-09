using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Common.Validation;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Events.GetEvents;

public sealed class GetEventsHandler
{
    public const string CacheKey = "query-cache:events:list:v3";

    private readonly IEventRepository _eventRepository;
    private readonly IQueryCache _queryCache;
    private readonly QueryCacheOptions _queryCacheOptions;
    private readonly IValidator<PageRequest> _validator;

    public GetEventsHandler(IEventRepository eventRepository, IQueryCache queryCache, QueryCacheOptions queryCacheOptions, IValidator<PageRequest> validator)
    {
        _eventRepository = eventRepository;
        _queryCache = queryCache;
        _queryCacheOptions = queryCacheOptions;
        _validator = validator;
    }

    public async Task<Result<PagedResultDto<EventDto>>> HandleAsync(PageRequest pageRequest, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(pageRequest, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<PagedResultDto<EventDto>>.Failure(validation.ToValidationError());
        }

        var all = await GetAllOrderedAsync(cancellationToken);
        var items = all.Skip((pageRequest.Page - 1) * pageRequest.PageSize).Take(pageRequest.PageSize).ToList();
        return Result<PagedResultDto<EventDto>>.Success(new PagedResultDto<EventDto>(items, pageRequest.Page, pageRequest.PageSize, all.Count));
    }

    public async Task<IReadOnlyList<EventDto>> GetAllOrderedAsync(CancellationToken cancellationToken)
    {
        var cached = await _queryCache.GetAsync<IReadOnlyList<EventDto>>(CacheKey, cancellationToken);
        if (cached.IsHit)
        {
            return cached.Value!;
        }

        var events = await _eventRepository.GetAllAsync(cancellationToken);
        // false < true 讓 CreatedAtUtc 為 null 的舊活動排最後，不依賴資料庫的 NULL 排序；排序後才寫入快取，命中路徑因此不必再排序。
        IReadOnlyList<EventDto> dtos = events
            .OrderBy(e => e.CreatedAtUtc == null)
            .ThenByDescending(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .Select(e => new EventDto(
                e.Id,
                e.Title,
                e.StartAtUtc,
                e.VenueId,
                e.SeatMapId,
                e.Description,
                e.PosterUrl,
                e.MaxTicketsPerOrder,
                e.IsQueueModeEnabled,
                e.IsRealNameRequired,
                e.SalesStartAtUtc,
                e.SalesEndAtUtc))
            .ToList();

        await _queryCache.SetAsync(CacheKey, dtos, TimeSpan.FromSeconds(_queryCacheOptions.EventListTtlSeconds), cancellationToken);
        return dtos;
    }
}
