namespace ProjectC.Application.Orders.PlaceOrder;

public sealed record PlaceOrderRequest(IReadOnlyList<PlaceOrderSelectionRequest> Selections);

public sealed record PlaceOrderSelectionRequest(Guid EventSeatId, Guid TicketTypeId);
