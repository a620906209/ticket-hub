using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Payments;

namespace ProjectC.Application.Orders;

public sealed class ConfirmOrderHandler
{
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IPaymentGateway _paymentGateway;

    public ConfirmOrderHandler(IDateTimeProvider dateTimeProvider, IPaymentGateway paymentGateway)
    {
        _dateTimeProvider = dateTimeProvider;
        _paymentGateway = paymentGateway;
    }

    public async Task<Result> Handle(Order order, IReadOnlyDictionary<Guid, EventSeat> eventSeatsById, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;

        if (order.Status != OrderStatus.Pending)
            return Result.Failure(Error.Conflict($"Order '{order.Id}' is not pending."));

        if (now >= order.HeldUntilUtc)
            return Result.Failure(Error.Conflict($"Order '{order.Id}' has expired."));

        var seats = new List<EventSeat>();
        foreach (var item in order.Items)
        {
            if (!eventSeatsById.TryGetValue(item.EventSeatId, out var seat))
                return Result.Failure(Error.NotFound($"Seat '{item.EventSeatId}' could not be found."));

            if (seat.EventId != order.EventId)
                return Result.Failure(Error.Conflict($"Seat '{item.EventSeatId}' does not belong to event '{order.EventId}'."));

            if (!seat.IsHeldBy(order.Id, now))
                return Result.Failure(Error.Conflict($"Seat '{item.EventSeatId}' is no longer held by order '{order.Id}'."));

            seats.Add(seat);
        }

        // 付款呼叫目前位於 OrderService.ChangeOrderStatusAsync 開啟的 DB transaction 內（座位悲觀鎖持有中），
        // 是刻意接受的技術債：Mock 沒有真正網路 I/O 所以無害，但真實金流串接時必須重新設計成交易外呼叫
        // + 回來後重新驗證 + 補償機制，不能沿用這裡的作法（見 order-payment-gateway-alignment design.md
        // Risks 小節第一項、決策 7）。例外不吞，讓 ChargeAsync 拋出的例外直接往外傳播給全域 IExceptionHandler。
        var amount = order.Items.Sum(i => i.UnitPrice);
        var paymentResult = await _paymentGateway.ChargeAsync(order.Id, amount, cancellationToken);
        if (paymentResult != PaymentResult.Succeeded)
            return Result.Failure(Error.Conflict($"Payment for order '{order.Id}' was declined."));

        foreach (var seat in seats)
            seat.ConfirmSold(order.Id, now);

        order.Confirm();
        return Result.Success();
    }
}
