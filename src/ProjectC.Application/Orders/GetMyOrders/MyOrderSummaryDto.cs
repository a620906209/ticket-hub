namespace ProjectC.Application.Orders.GetMyOrders;

public sealed record MyOrderSummaryDto(Guid Id, Guid EventId, string EventTitle, string Status, DateTime HeldUntilUtc);
