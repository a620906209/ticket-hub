namespace ProjectC.Application.Orders.GetOrders;

public sealed record OrderSummaryDto(
    Guid Id,
    Guid EventId,
    string EventTitle,
    Guid BuyerId,
    string BuyerDisplayName,
    int TicketCount,
    decimal TotalAmount,
    string Status,
    DateTime CreatedAtUtc,
    DateTime HeldUntilUtc);
