using ProjectC.Application.Common;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Orders;

/// <summary>同時處理使用者主動取消，以及查詢後發現已逾時（Pending 但 GetStatus 為 Expired）需要清理的訂單。</summary>
public sealed class CancelOrderHandler
{
    public Result Handle(Order order, IReadOnlyDictionary<Guid, EventSeat> eventSeatsById)
    {
        if (order.Status == OrderStatus.Confirmed)
            return Result.Failure(Error.Conflict($"Order '{order.Id}' is already confirmed and cannot be cancelled."));

        foreach (var item in order.Items)
        {
            if (eventSeatsById.TryGetValue(item.EventSeatId, out var seat))
                seat.ReleaseHold(order.Id);
        }

        order.Cancel();
        return Result.Success();
    }
}
