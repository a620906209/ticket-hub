using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Orders;

public sealed record SeatSelection(EventSeat EventSeat, TicketType TicketType);
