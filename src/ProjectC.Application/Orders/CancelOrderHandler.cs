using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Orders;

/// <summary>同時處理使用者主動取消，以及查詢後發現已逾時（Pending 但 GetStatus 為 Expired）需要清理的訂單。</summary>
public sealed class CancelOrderHandler
{
    private readonly IDateTimeProvider _dateTimeProvider;

    public CancelOrderHandler(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;
    }

    public Result Handle(
        Order order,
        IReadOnlyDictionary<Guid, EventSeat> eventSeatsById,
        IReadOnlyDictionary<Guid, TicketType> ticketTypesById)
    {
        if (order.Status != OrderStatus.Pending)
            return Result.Failure(Error.Conflict($"Order '{order.Id}' cannot be cancelled because its status is '{order.Status}'."));

        // 不一致狀態檢查只適用於座位項目：計數項目沒有「已被其他訂單售出」這類需要略過的競態場景
        // （庫存扣減/歸還是單純加減，不涉及其他訂單搶占同一份計數庫存的所有權判斷，design.md 決策 3）。
        foreach (var item in order.Items)
        {
            if (item.EventSeatId.HasValue
                && eventSeatsById.TryGetValue(item.EventSeatId.Value, out var seat)
                && seat.IsSoldBy(order.Id))
            {
                return Result.Failure(Error.Conflict(
                    $"Order '{order.Id}' is inconsistent: seat '{item.EventSeatId}' is already sold by this same order while the order is still Pending."));
            }
        }

        var now = _dateTimeProvider.UtcNow;
        foreach (var item in order.Items)
        {
            if (!item.EventSeatId.HasValue)
            {
                // 計數項目：無條件歸還，重複 Release（例如同一筆訂單被取消兩次）已經被上方
                // order.Status != Pending 的檢查擋下，不需要在這裡重複防護（design.md 決策 3）。
                if (ticketTypesById.TryGetValue(item.TicketTypeId!.Value, out var ticketType))
                    ticketType.Release(item.Quantity);

                continue;
            }

            if (!eventSeatsById.TryGetValue(item.EventSeatId.Value, out var seat))
                continue;

            if (seat.GetStatus(now) == EventSeatStatus.Sold)
                continue;

            seat.ReleaseHold(order.Id);
        }

        order.Cancel();
        return Result.Success();
    }
}
