namespace ProjectC.Application.Orders.GetOrderById;

public sealed record OrderDetailDto(
    Guid Id,
    Guid EventId,
    Guid BuyerId,
    string Status,
    DateTime HeldUntilUtc,
    IReadOnlyList<OrderItemDto> Items);

public sealed record OrderItemDto(Guid Id, Guid EventSeatId, decimal UnitPrice);
