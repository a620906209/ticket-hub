namespace ProjectC.Domain.Orders;

public sealed record OrderPage(IReadOnlyList<Order> Items, int TotalCount);
