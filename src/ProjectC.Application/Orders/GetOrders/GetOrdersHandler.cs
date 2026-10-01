using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders.GetOrders;

public sealed class GetOrdersHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly IMemberDisplayNameReader _memberDisplayNameReader;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetOrdersHandler(
        IOrderRepository orderRepository,
        IMemberDisplayNameReader memberDisplayNameReader,
        IDateTimeProvider dateTimeProvider)
    {
        _orderRepository = orderRepository;
        _memberDisplayNameReader = memberDisplayNameReader;
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

        var now = _dateTimeProvider.UtcNow;

        return orders
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.EventId,
                o.BuyerId,
                displayNamesByBuyerId[o.BuyerId],
                o.GetStatus(now).ToString(),
                o.HeldUntilUtc))
            .ToList();
    }
}
