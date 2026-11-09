using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Common.Validation;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Events.GetAdminEvents;

public sealed class GetAdminEventsHandler
{
    private readonly IEventRepository _eventRepository;
    private readonly IApplicationDbContext _dbContext;
    private readonly IEventSeatRepository _eventSeatRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IValidator<PageRequest> _validator;

    public GetAdminEventsHandler(
        IEventRepository eventRepository,
        IApplicationDbContext dbContext,
        IEventSeatRepository eventSeatRepository,
        IDateTimeProvider dateTimeProvider,
        IValidator<PageRequest> validator)
    {
        _eventRepository = eventRepository;
        _dbContext = dbContext;
        _eventSeatRepository = eventSeatRepository;
        _dateTimeProvider = dateTimeProvider;
        _validator = validator;
    }

    public async Task<Result<PagedResultDto<AdminEventSummaryDto>>> HandleAsync(Guid organizerId, PageRequest pageRequest, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(pageRequest, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<PagedResultDto<AdminEventSummaryDto>>.Failure(validation.ToValidationError());
        }

        var skip = (pageRequest.Page - 1) * pageRequest.PageSize;
        var eventPage = await _eventRepository.GetPageByOrganizerIdAsync(organizerId, skip, pageRequest.PageSize, cancellationToken);
        var events = eventPage.Items;

        var memberIds = events
            .Where(e => e.CreatedByMemberId is not null)
            .Select(e => e.CreatedByMemberId!.Value)
            .Distinct()
            .ToList();
        var displayNamesByMemberId = memberIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.Members
                .Where(m => memberIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m.DisplayName, cancellationToken);

        var eventIds = events.Select(e => e.Id).ToList();
        var seats = await _eventSeatRepository.GetByEventIdsAsync(eventIds, cancellationToken);
        var now = _dateTimeProvider.UtcNow;
        var seatsByEventId = seats.ToLookup(s => s.EventId);

        var items = events
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
                    soldCount,
                    e.IsRealNameRequired,
                    e.SalesStartAtUtc,
                    e.SalesEndAtUtc);
            })
            .ToList();
        return Result<PagedResultDto<AdminEventSummaryDto>>.Success(new PagedResultDto<AdminEventSummaryDto>(items, pageRequest.Page, pageRequest.PageSize, eventPage.TotalCount));
    }
}
