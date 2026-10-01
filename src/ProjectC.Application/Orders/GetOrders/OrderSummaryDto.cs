namespace ProjectC.Application.Orders.GetOrders;

public sealed record OrderSummaryDto(Guid Id, Guid EventId, Guid BuyerId, string BuyerDisplayName, string Status, DateTime HeldUntilUtc);
