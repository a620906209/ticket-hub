using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders.GetMyOrders;

public sealed class GetMyOrdersHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetMyOrdersHandler(IOrderRepository orderRepository, IDateTimeProvider dateTimeProvider)
    {
        _orderRepository = orderRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<IReadOnlyList<MyOrderSummaryDto>> HandleAsync(Guid buyerId, CancellationToken cancellationToken)
    {
        var orders = await _orderRepository.GetByBuyerIdAsync(buyerId, cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        return orders
            .Select(order => new MyOrderSummaryDto(order.Id, order.EventId, order.GetStatus(now).ToString(), order.HeldUntilUtc))
            .ToList();
    }
}
