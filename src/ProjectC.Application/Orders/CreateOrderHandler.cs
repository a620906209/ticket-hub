using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Common;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders;

public sealed class CreateOrderHandler
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(10);

    private readonly IDateTimeProvider _dateTimeProvider;

    public CreateOrderHandler(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;
    }

    public Result<Order> Handle(IReadOnlyList<SeatSelection> selections)
    {
        if (selections.Count == 0)
            return Result<Order>.Failure(Error.Validation("At least one seat must be selected."));

        var now = _dateTimeProvider.UtcNow;
        var orderId = Guid.NewGuid();
        var heldUntilUtc = now.Add(HoldDuration);

        var heldSeats = new List<EventSeat>();

        foreach (var selection in selections)
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

        var items = selections
            .Select(selection => new OrderItem(Guid.NewGuid(), selection.EventSeat.Id, selection.TicketType.Price))
            .ToList();

        var order = new Order(orderId, heldUntilUtc, items);
        return Result<Order>.Success(order);
    }
}
