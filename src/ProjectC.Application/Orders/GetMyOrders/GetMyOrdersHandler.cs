using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Common.Validation;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders.GetMyOrders;

public sealed class GetMyOrdersHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly IEventRepository _eventRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IValidator<PageRequest> _validator;

    public GetMyOrdersHandler(IOrderRepository orderRepository, IEventRepository eventRepository, IDateTimeProvider dateTimeProvider, IValidator<PageRequest> validator)
    {
        _orderRepository = orderRepository;
        _eventRepository = eventRepository;
        _dateTimeProvider = dateTimeProvider;
        _validator = validator;
    }

    public async Task<Result<PagedResultDto<MyOrderSummaryDto>>> HandleAsync(Guid buyerId, PageRequest pageRequest, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(pageRequest, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<PagedResultDto<MyOrderSummaryDto>>.Failure(validation.ToValidationError());
        }

        var skip = (pageRequest.Page - 1) * pageRequest.PageSize;
        var orderPage = await _orderRepository.GetPageByBuyerIdAsync(buyerId, skip, pageRequest.PageSize, cancellationToken);
        var orders = orderPage.Items;
        if (orders.Count == 0)
        {
            return Result<PagedResultDto<MyOrderSummaryDto>>.Success(
                new PagedResultDto<MyOrderSummaryDto>([], pageRequest.Page, pageRequest.PageSize, orderPage.TotalCount));
        }

        var eventIds = orders.Select(order => order.EventId).Distinct().ToList();
        var eventTitlesById = (await _eventRepository.GetByIdsAsync(eventIds, cancellationToken))
            .ToDictionary(@event => @event.Id, @event => @event.Title);

        // 訂單的 EventId 有 FK 且系統沒有刪除端點，查不到代表資料損毀：大聲失敗交由全域例外處理轉 500，
        // 訊息帶 Id 供維運定位（只進 log，不進 ProblemDetails）——order-display-enrichment design.md 決策 2。
        var orderWithMissingEvent = orders.FirstOrDefault(order => !eventTitlesById.ContainsKey(order.EventId));
        if (orderWithMissingEvent is not null)
        {
            throw new InvalidOperationException(
                $"Order '{orderWithMissingEvent.Id}' references Event '{orderWithMissingEvent.EventId}' that does not exist.");
        }

        var now = _dateTimeProvider.UtcNow;

        var items = orders
            .Select(order => new MyOrderSummaryDto(
                order.Id,
                order.EventId,
                eventTitlesById[order.EventId],
                order.GetStatus(now).ToString(),
                order.HeldUntilUtc))
            .ToList();
        return Result<PagedResultDto<MyOrderSummaryDto>>.Success(new PagedResultDto<MyOrderSummaryDto>(items, pageRequest.Page, pageRequest.PageSize, orderPage.TotalCount));
    }
}
