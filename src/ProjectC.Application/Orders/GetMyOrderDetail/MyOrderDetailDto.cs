namespace ProjectC.Application.Orders.GetMyOrderDetail;

public sealed record MyOrderDetailDto(
    Guid Id,
    Guid EventId,
    string Status,
    DateTime HeldUntilUtc,
    IReadOnlyList<MyOrderItemDto> Items);

public sealed record MyOrderItemDto(
    Guid Id,
    Guid? EventSeatId,
    Guid? TicketTypeId,
    int Quantity,
    decimal UnitPrice,
    IReadOnlyList<MyTicketDto> Tickets);

public sealed record MyTicketDto(Guid Id, string Status);
