using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Common;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Orders;

public sealed class CreateOrderHandler
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(10);

    private readonly IDateTimeProvider _dateTimeProvider;

    public CreateOrderHandler(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;
    }

    public Result<Order> Handle(Guid buyerId, IReadOnlyList<SeatSelection> seatSelections, IReadOnlyList<QuantitySelection> quantitySelections)
    {
        if (seatSelections.Count == 0 && quantitySelections.Count == 0)
            return Result<Order>.Failure(Error.Validation("At least one seat or ticket type must be selected."));

        var distinctEventIds = seatSelections.Select(s => s.EventSeat.EventId)
            .Concat(quantitySelections.Select(s => s.TicketType.EventId))
            .Distinct()
            .ToList();
        if (distinctEventIds.Count > 1)
            return Result<Order>.Failure(Error.Validation("All selected items must belong to the same event."));

        if (seatSelections.Select(s => s.EventSeat.Id).Distinct().Count() != seatSelections.Count)
            return Result<Order>.Failure(Error.Validation("The same seat cannot be selected more than once."));

        if (seatSelections.Any(s => s.EventSeat.EventId != s.TicketType.EventId))
            return Result<Order>.Failure(Error.Validation("Ticket type does not belong to the same event as the selected seat."));

        var eventId = distinctEventIds[0];
        var now = _dateTimeProvider.UtcNow;
        var orderId = Guid.NewGuid();
        var heldUntilUtc = now.Add(HoldDuration);

        var heldSeats = new List<EventSeat>();
        var reservedQuantities = new List<(TicketType TicketType, int Quantity)>();

        foreach (var selection in seatSelections)
        {
            try
            {
                selection.EventSeat.Hold(orderId, heldUntilUtc, now);
                heldSeats.Add(selection.EventSeat);
            }
            catch (DomainException)
            {
                foreach (var heldSeat in heldSeats)
                    heldSeat.ReleaseHold(orderId);

                return Result<Order>.Failure(Error.Conflict($"Seat '{selection.EventSeat.Id}' is no longer available."));
            }
        }

        foreach (var selection in quantitySelections)
        {
            try
            {
                selection.TicketType.Reserve(selection.Quantity);
                reservedQuantities.Add((selection.TicketType, selection.Quantity));
            }
            catch (DomainException)
            {
                // 任一計數項目扣減失敗時，本次已鎖定的座位與已扣減的計數庫存 MUST 全數復原
                // （design.md 決策 3／ticket-ordering spec「純計數票種庫存不足」Scenario）。
                foreach (var heldSeat in heldSeats)
                    heldSeat.ReleaseHold(orderId);
                foreach (var (ticketType, quantity) in reservedQuantities)
                    ticketType.Release(quantity);

                return Result<Order>.Failure(Error.Conflict($"Ticket type '{selection.TicketType.Id}' does not have enough inventory."));
            }
        }

        var seatItems = seatSelections
            .Select(selection => new OrderItem(Guid.NewGuid(), selection.TicketType.Id, selection.EventSeat.Id, 1, selection.TicketType.Price));
        var quantityItems = quantitySelections
            .Select(selection => new OrderItem(Guid.NewGuid(), selection.TicketType.Id, null, selection.Quantity, selection.TicketType.Price));
        var items = seatItems.Concat(quantityItems).ToList();

        var order = new Order(orderId, eventId, buyerId, heldUntilUtc, items);
        return Result<Order>.Success(order);
    }
}
