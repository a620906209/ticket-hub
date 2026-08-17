using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders.GetOrderById;

public sealed class GetOrderByIdHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetOrderByIdHandler(IOrderRepository orderRepository, IDateTimeProvider dateTimeProvider)
    {
        _orderRepository = orderRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<OrderDetailDto>> HandleAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Result<OrderDetailDto>.Failure(Error.NotFound($"Order '{orderId}' was not found."));
        }

        var now = _dateTimeProvider.UtcNow;
        var items = order.Items.Select(i => new OrderItemDto(i.Id, i.EventSeatId, i.UnitPrice)).ToList();
        var dto = new OrderDetailDto(order.Id, order.EventId, order.BuyerId, order.GetStatus(now).ToString(), order.HeldUntilUtc, items);

        return Result<OrderDetailDto>.Success(dto);
    }
}
