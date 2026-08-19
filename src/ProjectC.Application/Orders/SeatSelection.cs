using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Orders;

public sealed record SeatSelection(EventSeat EventSeat, TicketType TicketType);

/// <summary>純計數（不綁座位）票種的選購項目，供 CreateOrderHandler 與 SeatSelection 並存處理。</summary>
public sealed record QuantitySelection(TicketType TicketType, int Quantity);
