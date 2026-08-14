using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders;

/// <summary>同時處理使用者主動取消，以及查詢後發現已逾時（Pending 但 GetStatus 為 Expired）需要清理的訂單。</summary>
public sealed class CancelOrderHandler
{
    private readonly IDateTimeProvider _dateTimeProvider;

    public CancelOrderHandler(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;
    }

    public Result Handle(Order order, IReadOnlyDictionary<Guid, EventSeat> eventSeatsById)
    {
        if (order.Status != OrderStatus.Pending)
            return Result.Failure(Error.Conflict($"Order '{order.Id}' cannot be cancelled because its status is '{order.Status}'."));

        var now = _dateTimeProvider.UtcNow;

        foreach (var item in order.Items)
        {
            if (eventSeatsById.TryGetValue(item.EventSeatId, out var seat) && seat.GetStatus(now) == EventSeatStatus.Sold)
                return Result.Failure(Error.Conflict($"Seat '{item.EventSeatId}' has already been sold and cannot be released."));
        }

        foreach (var item in order.Items)
        {
            if (eventSeatsById.TryGetValue(item.EventSeatId, out var seat))
                seat.ReleaseHold(order.Id);
        }

        order.Cancel();
        return Result.Success();
    }
}
