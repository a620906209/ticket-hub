using ProjectC.Application.Common;

namespace ProjectC.Application.Orders;

/// <summary>
/// 下單 409 的訊息單一定義：交易前提早判斷（<see cref="OrderService"/>）與鎖內權威判斷（<see cref="CreateOrderHandler"/>）共用，
/// 呼叫端看到的訊息不因在哪一層被拒而不同（order-placement-p95-optimization design.md 決策 5）。
/// </summary>
public static class PlaceOrderConflictErrors
{
    public static Error SeatNoLongerAvailable(Guid eventSeatId)
        => Error.Conflict($"Seat '{eventSeatId}' is no longer available.");

    public static Error TicketTypeInventoryInsufficient(Guid ticketTypeId)
        => Error.Conflict($"Ticket type '{ticketTypeId}' does not have enough inventory.");
}
