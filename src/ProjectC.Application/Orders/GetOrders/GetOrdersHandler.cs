using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders.GetOrders;

public sealed class GetOrdersHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetOrdersHandler(IOrderRepository orderRepository, IDateTimeProvider dateTimeProvider)
    {
        _orderRepository = orderRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<IReadOnlyList<OrderSummaryDto>> HandleAsync(CancellationToken cancellationToken)
    {
        var orders = await _orderRepository.GetAllAsync(cancellationToken);
        var now = _dateTimeProvider.UtcNow;

        return orders
            .Select(o => new OrderSummaryDto(o.Id, o.EventId, o.BuyerId, o.GetStatus(now).ToString(), o.HeldUntilUtc))
            .ToList();
    }
}
