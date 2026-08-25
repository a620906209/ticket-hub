using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Payments;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Orders;

public sealed class ConfirmOrderHandler
{
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IPaymentGateway _paymentGateway;
    private readonly ITicketRepository _ticketRepository;

    public ConfirmOrderHandler(IDateTimeProvider dateTimeProvider, IPaymentGateway paymentGateway, ITicketRepository ticketRepository)
    {
        _dateTimeProvider = dateTimeProvider;
        _paymentGateway = paymentGateway;
        _ticketRepository = ticketRepository;
    }

    public async Task<Result> Handle(
        Order order,
        IReadOnlyDictionary<Guid, EventSeat> eventSeatsById,
        IReadOnlyDictionary<Guid, TicketType> ticketTypesById,
        CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;

        if (order.Status != OrderStatus.Pending)
            return Result.Failure(Error.Conflict($"Order '{order.Id}' is not pending."));

        if (now >= order.HeldUntilUtc)
            return Result.Failure(Error.Conflict($"Order '{order.Id}' has expired."));

        var seats = new List<EventSeat>();
        foreach (var item in order.Items)
        {
            if (!item.EventSeatId.HasValue)
            {
                // 計數項目：庫存已在建立訂單當下扣減（Reserve），確認訂單時 MUST NOT 再次扣減，
                // 呼叫端鎖 TicketType 只是為了序列化並發確認，這裡不需要額外驗證數量
                // （design.md 決策 3）。仍檢查對應 TicketType 是否存在，跟座位項目的檢查方式對稱。
                if (!ticketTypesById.ContainsKey(item.TicketTypeId!.Value))
                    return Result.Failure(Error.NotFound($"Ticket type '{item.TicketTypeId}' could not be found."));

                continue;
            }

            if (!eventSeatsById.TryGetValue(item.EventSeatId.Value, out var seat))
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
        // 金額計算 MUST 乘以 Quantity——計數項目一筆 OrderItem 可能代表多張，座位項目 Quantity 固定 1，
        // 語意自然相容（design.md Risks，外部審查抓到）。
        var amount = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        var paymentResult = await _paymentGateway.ChargeAsync(order.Id, amount, cancellationToken);
        if (paymentResult != PaymentResult.Succeeded)
            return Result.Failure(Error.Conflict($"Payment for order '{order.Id}' was declined."));

        foreach (var seat in seats)
            seat.ConfirmSold(order.Id, now);

        order.Confirm();

        // MUST NOT 呼叫 ITicketSigningService 或 QR 圖檔服務——QR 內容/圖檔為按需產生，不在出票交易內產生（design.md 決策 1）。
        foreach (var item in order.Items)
        {
            // 座位項目的 Quantity MUST 固定為 1（見上方金額計算註解與 CreateOrderHandler），
            // 一張票綁一個座位；此處防呆是為了避免上游若日後出現 Quantity 計算錯誤時，
            // 靜默對同一個座位發出多張票。
            if (item.EventSeatId.HasValue && item.Quantity != 1)
                throw new InvalidOperationException($"Seat-backed order item '{item.Id}' has quantity {item.Quantity}, expected 1.");

            for (var i = 0; i < item.Quantity; i++)
                _ticketRepository.Add(new Ticket(Guid.NewGuid(), item.Id, now));
        }

        return Result.Success();
    }
}
