using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders.GetOrderById;

public sealed class GetOrderByIdHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly IEventRepository _eventRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetOrderByIdHandler(IOrderRepository orderRepository, IEventRepository eventRepository, IDateTimeProvider dateTimeProvider)
    {
        _orderRepository = orderRepository;
        _eventRepository = eventRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<OrderDetailDto>> HandleAsync(Guid orderId, Guid organizerId, CancellationToken cancellationToken)
    {
        // 不存在與不屬於呼叫端 Organizer 共用同一個 Error，避免以不同回應洩漏訂單存在性（ORD-DETAIL-003）。
        var notFound = Error.NotFound($"Order '{orderId}' was not found.");

        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Result<OrderDetailDto>.Failure(notFound);
        }

        // Orders.EventId 有 FK，查無活動代表資料毀損；不可當成 404 或放行（ORD-DETAIL-004）。
        var @event = await _eventRepository.GetByIdAsync(order.EventId, cancellationToken)
            ?? throw new InvalidOperationException($"Event '{order.EventId}' referenced by order '{orderId}' was not found.");

        if (@event.OrganizerId != organizerId)
        {
            return Result<OrderDetailDto>.Failure(notFound);
        }

        var now = _dateTimeProvider.UtcNow;
        var items = order.Items.Select(i => new OrderItemDto(i.Id, i.EventSeatId, i.TicketTypeId, i.Quantity, i.UnitPrice)).ToList();
        var dto = new OrderDetailDto(order.Id, order.EventId, order.BuyerId, order.GetStatus(now).ToString(), order.HeldUntilUtc, items);

        return Result<OrderDetailDto>.Success(dto);
    }
}
