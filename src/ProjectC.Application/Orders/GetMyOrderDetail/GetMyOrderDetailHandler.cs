using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Orders.GetMyOrderDetail;

public sealed class GetMyOrderDetailHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly ITicketRepository _ticketRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetMyOrderDetailHandler(
        IOrderRepository orderRepository,
        ITicketRepository ticketRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _orderRepository = orderRepository;
        _ticketRepository = ticketRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<MyOrderDetailDto>> HandleAsync(Guid orderId, Guid callerBuyerId, CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Result<MyOrderDetailDto>.Failure(Error.NotFound($"Order '{orderId}' was not found."));
        }

        if (order.BuyerId != callerBuyerId)
        {
            return Result<MyOrderDetailDto>.Failure(Error.Forbidden("You are not the buyer of this order."));
        }

        var orderItemIds = order.Items.Select(item => item.Id).ToList();
        var ticketsByOrderItemId = (await _ticketRepository.GetByOrderItemIdsAsync(orderItemIds, cancellationToken))
            .GroupBy(ticket => ticket.OrderItemId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MyTicketDto>)group
                .Select(ticket => new MyTicketDto(ticket.Id, ticket.Status.ToString()))
                .ToList());
        var items = order.Items
            .Select(item => new MyOrderItemDto(
                item.Id,
                item.EventSeatId,
                item.TicketTypeId,
                item.Quantity,
                item.UnitPrice,
                ticketsByOrderItemId.GetValueOrDefault(item.Id, [])))
            .ToList();
        var dto = new MyOrderDetailDto(
            order.Id,
            order.EventId,
            order.GetStatus(_dateTimeProvider.UtcNow).ToString(),
            order.HeldUntilUtc,
            items);

        return Result<MyOrderDetailDto>.Success(dto);
    }
}
