using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Common.Validation;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders.GetOrders;

public sealed class GetOrdersHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly IMemberDisplayNameReader _memberDisplayNameReader;
    private readonly IEventRepository _eventRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IValidator<PageRequest> _validator;

    public GetOrdersHandler(
        IOrderRepository orderRepository,
        IMemberDisplayNameReader memberDisplayNameReader,
        IEventRepository eventRepository,
        IDateTimeProvider dateTimeProvider,
        IValidator<PageRequest> validator)
    {
        _orderRepository = orderRepository;
        _memberDisplayNameReader = memberDisplayNameReader;
        _eventRepository = eventRepository;
        _dateTimeProvider = dateTimeProvider;
        _validator = validator;
    }

    public async Task<Result<PagedResultDto<OrderSummaryDto>>> HandleAsync(Guid organizerId, PageRequest pageRequest, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(pageRequest, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<PagedResultDto<OrderSummaryDto>>.Failure(validation.ToValidationError());
        }

        var skip = (pageRequest.Page - 1) * pageRequest.PageSize;
        var orderPage = await _orderRepository.GetPageByOrganizerIdAsync(organizerId, skip, pageRequest.PageSize, cancellationToken);
        var orders = orderPage.Items;
        if (orders.Count == 0)
        {
            return Result<PagedResultDto<OrderSummaryDto>>.Success(
                new PagedResultDto<OrderSummaryDto>([], pageRequest.Page, pageRequest.PageSize, orderPage.TotalCount));
        }

        var buyerIds = orders.Select(o => o.BuyerId).Distinct().ToList();
        var displayNamesByBuyerId = await _memberDisplayNameReader.GetDisplayNamesByIdsAsync(buyerIds, cancellationToken);

        // 與 GetAdminEventsHandler 對 CreatedByMemberId 查不到回 null 不同：BuyerId 是非 null FK，
        // 查不到代表資料損毀，大聲失敗交由全域例外處理轉 500（order-display-enrichment design.md 決策 2）。
        var orderWithMissingBuyer = orders.FirstOrDefault(o => !displayNamesByBuyerId.ContainsKey(o.BuyerId));
        if (orderWithMissingBuyer is not null)
        {
            throw new InvalidOperationException(
                $"Order '{orderWithMissingBuyer.Id}' references buyer Member '{orderWithMissingBuyer.BuyerId}' that does not exist.");
        }

        var eventIds = orders.Select(o => o.EventId).Distinct().ToList();
        var eventTitlesById = (await _eventRepository.GetByIdsAsync(eventIds, cancellationToken))
            .ToDictionary(@event => @event.Id, @event => @event.Title);

        // 比照 GetMyOrdersHandler：EventId 有 FK 且沒有刪除活動的端點，查不到即資料損毀（admin-order-list-enrichment design.md 決策 3）。
        var orderWithMissingEvent = orders.FirstOrDefault(o => !eventTitlesById.ContainsKey(o.EventId));
        if (orderWithMissingEvent is not null)
        {
            throw new InvalidOperationException(
                $"Order '{orderWithMissingEvent.Id}' references Event '{orderWithMissingEvent.EventId}' that does not exist.");
        }

        var now = _dateTimeProvider.UtcNow;

        // 排序（CreatedAtUtc 新到舊、Id 升冪）與切頁都在 Repository 的資料庫查詢完成，此處保留當頁順序。
        var items = orders
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.EventId,
                eventTitlesById[o.EventId],
                o.BuyerId,
                displayNamesByBuyerId[o.BuyerId],
                o.GetTicketCount(),
                o.GetTotalAmount(),
                o.GetStatus(now).ToString(),
                o.CreatedAtUtc,
                o.HeldUntilUtc))
            .ToList();
        return Result<PagedResultDto<OrderSummaryDto>>.Success(new PagedResultDto<OrderSummaryDto>(items, pageRequest.Page, pageRequest.PageSize, orderPage.TotalCount));
    }
}
