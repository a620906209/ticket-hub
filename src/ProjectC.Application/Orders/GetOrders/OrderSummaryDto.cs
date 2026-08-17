namespace ProjectC.Application.Orders.GetOrders;

public sealed record OrderSummaryDto(Guid Id, Guid EventId, Guid BuyerId, string Status, DateTime HeldUntilUtc);
