using ProjectC.Application.Common.Interfaces;
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

    public GetOrdersHandler(
        IOrderRepository orderRepository,
        IMemberDisplayNameReader memberDisplayNameReader,
        IEventRepository eventRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _orderRepository = orderRepository;
        _memberDisplayNameReader = memberDisplayNameReader;
        _eventRepository = eventRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<IReadOnlyList<OrderSummaryDto>> HandleAsync(Guid organizerId, CancellationToken cancellationToken)
    {
        var orders = await _orderRepository.GetByOrganizerIdAsync(organizerId, cancellationToken);
        if (orders.Count == 0)
        {
            return [];
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

        // 列表沒有分頁、資料已全部載入，在記憶體排序即可；Id 為次要鍵讓同時建立的訂單順序固定（design.md 決策 5）。
        return orders
            .OrderByDescending(o => o.CreatedAtUtc)
            .ThenBy(o => o.Id)
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
    }
}
