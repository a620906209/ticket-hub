using ProjectC.Application.Common;
using ProjectC.Domain.Common;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders;

public sealed class ConfirmOrderHandler
{
    private readonly IDateTimeProvider _dateTimeProvider;

    public ConfirmOrderHandler(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;
    }

    public Result Handle(Order order, IReadOnlyDictionary<Guid, EventSeat> eventSeatsById)
    {
        var now = _dateTimeProvider.UtcNow;

        if (order.Status != OrderStatus.Pending)
            return Result.Failure($"Order '{order.Id}' is not pending.");

        if (now >= order.HeldUntilUtc)
            return Result.Failure($"Order '{order.Id}' has expired.");

        var seats = new List<EventSeat>();
        foreach (var item in order.Items)
        {
            if (!eventSeatsById.TryGetValue(item.EventSeatId, out var seat))
                return Result.Failure($"Seat '{item.EventSeatId}' could not be found.");

            if (!seat.IsHeldBy(order.Id, now))
                return Result.Failure($"Seat '{item.EventSeatId}' is no longer held by order '{order.Id}'.");

            seats.Add(seat);
        }

        foreach (var seat in seats)
            seat.ConfirmSold(order.Id, now);

        order.Confirm();
        return Result.Success();
    }
}
