namespace ProjectC.Application.Orders.GetOrderById;

public sealed record OrderDetailDto(
    Guid Id,
    Guid EventId,
    Guid BuyerId,
    string Status,
    DateTime HeldUntilUtc,
    IReadOnlyList<OrderItemDto> Items);

// EventSeatId／TicketTypeId 都是 Guid?——既有舊訂單的 TicketTypeId 可能是 NULL（不回填，見
// design.md Migration Plan），DTO 必須能忠實回傳 null，不能查詢失敗或塞假值（design.md 決策 2）。
public sealed record OrderItemDto(Guid Id, Guid? EventSeatId, Guid? TicketTypeId, int Quantity, decimal UnitPrice);
