using FluentValidation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Orders;

public sealed class OrderService
{
    private readonly ITicketTypeRepository _ticketTypeRepository;
    private readonly IEventSeatRepository _eventSeatRepository;
    private readonly IEventRepository _eventRepository;
    private readonly ISeatMapRepository _seatMapRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<PlaceOrderRequest> _validator;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CreateOrderHandler _createOrderHandler;
    private readonly ConfirmOrderHandler _confirmOrderHandler;
    private readonly CancelOrderHandler _cancelOrderHandler;

    public OrderService(
        ITicketTypeRepository ticketTypeRepository,
        IEventSeatRepository eventSeatRepository,
        IEventRepository eventRepository,
        ISeatMapRepository seatMapRepository,
        IOrderRepository orderRepository,
        IUnitOfWork unitOfWork,
        IValidator<PlaceOrderRequest> validator,
        IDateTimeProvider dateTimeProvider,
        CreateOrderHandler createOrderHandler,
        ConfirmOrderHandler confirmOrderHandler,
        CancelOrderHandler cancelOrderHandler)
    {
        _ticketTypeRepository = ticketTypeRepository;
        _eventSeatRepository = eventSeatRepository;
        _eventRepository = eventRepository;
        _seatMapRepository = seatMapRepository;
        _orderRepository = orderRepository;
        _unitOfWork = unitOfWork;
        _validator = validator;
        _dateTimeProvider = dateTimeProvider;
        _createOrderHandler = createOrderHandler;
        _confirmOrderHandler = confirmOrderHandler;
        _cancelOrderHandler = cancelOrderHandler;
    }

    public async Task<Result<Guid>> PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        // no-tracking 查詢，純粹用於存在性／RequiresSeat 交叉驗證，MUST NOT 對這裡取得的實例呼叫
        // TicketType.Reserve()（design.md 決策 3——之後的 GetForUpdateAsync 才是鎖定後的版本）。
        var ticketTypesById = new Dictionary<Guid, TicketType>();
        foreach (var ticketTypeId in request.Selections.Select(s => s.TicketTypeId).Distinct())
        {
            var ticketType = await _ticketTypeRepository.GetByIdAsync(ticketTypeId, cancellationToken);
            if (ticketType is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Ticket type '{ticketTypeId}' was not found."));
            }

            ticketTypesById[ticketTypeId] = ticketType;
        }

        // RequiresSeat 與請求形狀的交叉驗證：純計數票種指定了座位／綁座位票種未指定座位／
        // 座位項目指定非 1 的 Quantity 皆 MUST 拒絕（design.md 決策 4，ticket-purchase spec 三個 Scenario）。
        foreach (var selectionRequest in request.Selections)
        {
            var ticketType = ticketTypesById[selectionRequest.TicketTypeId];

            if (!ticketType.RequiresSeat && selectionRequest.EventSeatId.HasValue)
            {
                return Result<Guid>.Failure(Error.Validation(
                    $"Ticket type '{ticketType.Id}' does not require a seat but an event seat was specified."));
            }

            if (ticketType.RequiresSeat && !selectionRequest.EventSeatId.HasValue)
            {
                return Result<Guid>.Failure(Error.Validation(
                    $"Ticket type '{ticketType.Id}' requires a seat but none was specified."));
            }

            if (ticketType.RequiresSeat && selectionRequest.Quantity != 1)
            {
                return Result<Guid>.Failure(Error.Validation(
                    $"Seat item for ticket type '{ticketType.Id}' must have a quantity of exactly 1."));
            }
        }

        // 座位存在性／所屬活動比對也 MUST 在取得任何資料庫鎖之前完成，理由跟下面的跨活動檢查相同。
        // no-tracking 批次查詢，不可用 GetForUpdateAsync——那個方法會鎖定，且交易還沒開始也無法呼叫。
        var seatSelectionEventSeatIds = request.Selections
            .Where(s => s.EventSeatId.HasValue)
            .Select(s => s.EventSeatId!.Value)
            .Distinct()
            .ToList();
        var validationSeatsById = new Dictionary<Guid, EventSeat>();
        if (seatSelectionEventSeatIds.Count > 0)
        {
            var validationSeats = await _eventSeatRepository.GetByIdsAsync(seatSelectionEventSeatIds, cancellationToken);
            if (validationSeats.Count < seatSelectionEventSeatIds.Count)
            {
                return Result<Guid>.Failure(Error.NotFound("One or more selected seats were not found."));
            }

            validationSeatsById = validationSeats.ToDictionary(es => es.Id);
        }

        // 跨活動驗證 MUST 在取得任何資料庫鎖之前完成（ticket-ordering spec「建立訂單並原子性鎖定座位
        // 或扣減票種庫存」Requirement：「在嘗試鎖定或扣減任何項目之前，系統 MUST 先驗證...所有項目
        // 須全部屬於同一場活動」）——外部審查抓到兩輪問題：① 先前這裡只用「第一個票種的活動」查
        // 限購張數，真正的跨活動檢查延後到 CreateOrderHandler.Handle 才做；② 改成比對 ticketTypesById
        // 之後，仍只看票種的 EventId，沒把座位所屬活動納入比對集合——只有一個票種時（例如一個綁座位
        // 票種配另一場活動的座位），這個比對會通過，直到座位被鎖定後才被 `ticketType.EventId !=
        // eventSeat.EventId` 擋下。這裡改成「票種活動」跟「座位活動」的聯集：只要聯集大小是 1，數學上
        // 保證每個座位跟其配對票種的活動必然相同（若有任何一組不一致，聯集大小必然 >= 2），
        // 不需要額外逐一比對座位與其配對票種。
        var distinctEventIds = ticketTypesById.Values.Select(t => t.EventId)
            .Concat(validationSeatsById.Values.Select(es => es.EventId))
            .Distinct()
            .ToList();
        if (distinctEventIds.Count > 1)
        {
            return Result<Guid>.Failure(Error.Validation("All selected items must belong to the same event."));
        }

        // 每筆訂單限購張數：依 Quantity 加總（座位項目已在上面驗證固定為 1，語意自然相容）。
        // 上面的跨活動檢查已經保證所有票種屬於同一場活動，這裡不再是「任選第一個」，是唯一的活動。
        var orderEvent = await _eventRepository.GetByIdAsync(distinctEventIds[0], cancellationToken);
        if (orderEvent is { MaxTicketsPerOrder: { } maxTicketsPerOrder })
        {
            // Quantity 是外部輸入，validator 只保證 >= 1、沒有上限；Sum(int) 用的是 checked int 累加，
            // 兩個刻意送出接近 int.MaxValue 的 Quantity 就會拋 OverflowException、變成未預期的 500，
            // 而不是乾淨的驗證錯誤——改用 long 累加規避溢位（外部審查抓到）。
            var totalQuantity = request.Selections.Sum(s => (long)s.Quantity);
            if (totalQuantity > maxTicketsPerOrder)
            {
                return Result<Guid>.Failure(Error.Validation($"This event allows at most {maxTicketsPerOrder} ticket(s) per order."));
            }
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        // 鎖定順序（design.md 決策 3 固定規則）：先鎖座位，後鎖票種；純計數訂單沒有任何座位時，
        // MUST NOT 呼叫 GetForUpdateAsync(空清單)（該方法對空清單拋 ArgumentException）。
        var eventSeatIds = request.Selections.Where(s => s.EventSeatId.HasValue).Select(s => s.EventSeatId!.Value).ToList();
        var eventSeatsById = new Dictionary<Guid, EventSeat>();
        if (eventSeatIds.Count > 0)
        {
            var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
            if (eventSeats.Count < eventSeatIds.Count)
            {
                return Result<Guid>.Failure(Error.NotFound("One or more selected seats were not found."));
            }

            eventSeatsById = eventSeats.ToDictionary(es => es.Id);
        }

        // 只有純計數（RequiresSeat = false）票種需要鎖定：座位模式票種的庫存概念是 EventSeat 狀態機，
        // 不涉及 AvailableQuantity。TicketType 之間依 Id 排序鎖定（GetForUpdateAsync 內部保證），
        // 避免一筆訂單同時買多個不同計數票種時，跟其他並發交易鎖定順序不一致造成死鎖。
        var countingTicketTypeIds = request.Selections
            .Where(s => !s.EventSeatId.HasValue)
            .Select(s => s.TicketTypeId)
            .Distinct()
            .ToList();
        var lockedTicketTypesById = new Dictionary<Guid, TicketType>();
        if (countingTicketTypeIds.Count > 0)
        {
            var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, cancellationToken);
            if (lockedTicketTypes.Count < countingTicketTypeIds.Count)
            {
                return Result<Guid>.Failure(Error.NotFound("One or more selected ticket types were not found."));
            }

            lockedTicketTypesById = lockedTicketTypes.ToDictionary(t => t.Id);
        }

        // 分區比對：座位實際所屬分區 MUST 與所選票種的分區一致，防止用低價分區的票種配高價分區的座位
        // （見 ticketing-purchase design.md 決策 2 第 4 點）。座位圖依 EventId 快取，正常情況下所有
        // 項目屬於同一場活動只查一次；跨活動的狀況交給下面 CreateOrderHandler.Handle 再擋一次。
        var seatMapsByEventId = new Dictionary<Guid, SeatMap>();
        var seatSelections = new List<SeatSelection>();
        var quantitySelections = new List<QuantitySelection>();

        foreach (var selectionRequest in request.Selections)
        {
            var ticketType = ticketTypesById[selectionRequest.TicketTypeId];

            if (!selectionRequest.EventSeatId.HasValue)
            {
                // 計數項目：Reserve() MUST 對 GetForUpdateAsync 回傳的鎖定實例呼叫，不可用上面
                // no-tracking 查到的 ticketType（design.md 決策 3）。
                quantitySelections.Add(new QuantitySelection(lockedTicketTypesById[ticketType.Id], selectionRequest.Quantity));
                continue;
            }

            if (!eventSeatsById.TryGetValue(selectionRequest.EventSeatId.Value, out var eventSeat))
            {
                return Result<Guid>.Failure(Error.NotFound($"Seat '{selectionRequest.EventSeatId}' was not found."));
            }

            if (ticketType.EventId != eventSeat.EventId)
            {
                return Result<Guid>.Failure(Error.Validation("Ticket type does not belong to the same event as the selected seat."));
            }

            if (!seatMapsByEventId.TryGetValue(eventSeat.EventId, out var seatMap))
            {
                var @event = await _eventRepository.GetByIdAsync(eventSeat.EventId, cancellationToken);
                if (@event is null)
                {
                    return Result<Guid>.Failure(Error.NotFound($"Event '{eventSeat.EventId}' was not found."));
                }

                seatMap = await _seatMapRepository.GetByIdAsync(@event.SeatMapId, cancellationToken);
                if (seatMap is null)
                {
                    return Result<Guid>.Failure(Error.NotFound($"Seat map '{@event.SeatMapId}' was not found."));
                }

                seatMapsByEventId[eventSeat.EventId] = seatMap;
            }

            var seatTemplate = seatMap.Seats.FirstOrDefault(s => s.Id == eventSeat.SeatId);
            if (seatTemplate is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Seat '{eventSeat.SeatId}' was not found in the seat map."));
            }

            if (seatTemplate.ZoneCode != ticketType.ZoneCode)
            {
                return Result<Guid>.Failure(Error.Validation(
                    $"Seat '{eventSeat.Id}' belongs to zone '{seatTemplate.ZoneCode}', which does not match ticket type zone '{ticketType.ZoneCode}'."));
            }

            seatSelections.Add(new SeatSelection(eventSeat, ticketType));
        }

        var result = _createOrderHandler.Handle(buyerId, seatSelections, quantitySelections);
        if (!result.IsSuccess)
        {
            return Result<Guid>.Failure(result.Error!);
        }

        _orderRepository.Add(result.Value!);
        await transaction.CommitAsync(cancellationToken);

        return Result<Guid>.Success(result.Value!.Id);
    }

    public Task<Result> ConfirmOrderAsync(Guid orderId, Guid requestingBuyerId, CancellationToken cancellationToken)
        => ChangeOrderStatusAsync(orderId, requestingBuyerId, _confirmOrderHandler.Handle, cancellationToken);

    public Task<Result> CancelOrderAsync(Guid orderId, Guid requestingBuyerId, CancellationToken cancellationToken)
        => ChangeOrderStatusAsync(orderId, requestingBuyerId, WrapSync(_cancelOrderHandler.Handle), cancellationToken);

    /// <summary>
    /// 背景清理呼叫，沒有買家身份可驗證，改以「訂單確實已逾時」作為授權依據，取代本人驗證
    /// （見 ticketing-order-management design.md 決策 1）。
    /// </summary>
    public Task<Result> CancelExpiredOrderAsync(Guid orderId, CancellationToken cancellationToken)
        => ChangeOrderStatusAsync(orderId, requestingBuyerId: null, WrapSync(_cancelOrderHandler.Handle), cancellationToken);

    // CancelOrderHandler.Handle 維持同步（純記憶體邏輯，無 I/O），包一層轉成跟 ConfirmOrderHandler.Handle
    // 相同的非同步委派型別，讓兩者能共用同一套 ChangeOrderStatusAsync 交易骨架（見 design.md 決策 3）。
    private static Func<Order, IReadOnlyDictionary<Guid, EventSeat>, IReadOnlyDictionary<Guid, TicketType>, CancellationToken, Task<Result>> WrapSync(
        Func<Order, IReadOnlyDictionary<Guid, EventSeat>, IReadOnlyDictionary<Guid, TicketType>, Result> syncHandle)
        => (order, seats, ticketTypes, _) => Task.FromResult(syncHandle(order, seats, ticketTypes));

    private async Task<Result> ChangeOrderStatusAsync(
        Guid orderId,
        Guid? requestingBuyerId,
        Func<Order, IReadOnlyDictionary<Guid, EventSeat>, IReadOnlyDictionary<Guid, TicketType>, CancellationToken, Task<Result>> handle,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Result.Failure(Error.NotFound($"Order '{orderId}' was not found."));
        }

        if (requestingBuyerId is not null)
        {
            if (order.BuyerId != requestingBuyerId)
            {
                return Result.Failure(Error.Forbidden("You are not the buyer of this order."));
            }
        }
        else if (_dateTimeProvider.UtcNow < order.HeldUntilUtc)
        {
            // 系統呼叫（背景清理），沒有買家身份可驗證；用「訂單確實已逾時」取代本人驗證作為授權依據，
            // 避免這個方法被誤用成可以繞過買家授權、取消任何 Pending 訂單的工具
            // （見 ticketing-order-management design.md 決策 1）。
            return Result.Failure(Error.Conflict($"Order '{orderId}' is not yet expired."));
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        // 鎖定順序（design.md 決策 3）：先鎖座位、後鎖票種；純計數訂單（沒有任何座位行項）
        // MUST NOT 呼叫 GetForUpdateAsync(空清單)。
        var eventSeatIds = order.Items.Where(i => i.EventSeatId.HasValue).Select(i => i.EventSeatId!.Value).Distinct().ToList();
        var eventSeatsById = new Dictionary<Guid, EventSeat>();
        if (eventSeatIds.Count > 0)
        {
            var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
            if (eventSeats.Count < eventSeatIds.Count)
            {
                // 理論上不應該發生：目前系統沒有刪除 EventSeat 的路徑，order.Items 引用的座位建立後就一直存在。
                // 用 NotFound 而非 Conflict，跟 ConfirmOrderHandler 自己對「查不到座位」的既有分類一致
                // （見 src/ProjectC.Application/Orders/ConfirmOrderHandler.cs），維持 Confirm/Cancel 行為對稱。
                return Result.Failure(Error.NotFound($"One or more seats referenced by order '{orderId}' were not found."));
            }

            eventSeatsById = eventSeats.ToDictionary(es => es.Id);
        }

        // 計數行項對應的 TicketType 也要鎖：即使是 Confirm（不寫入 AvailableQuantity），純計數訂單
        // 在資料庫層唯一的序列化點就是這個鎖，省略會讓兩個並發 Confirm 都通過、造成重複收款
        // （design.md 決策 3 審查後補充的說明）。
        var ticketTypeIds = order.Items.Where(i => !i.EventSeatId.HasValue).Select(i => i.TicketTypeId!.Value).Distinct().ToList();
        var ticketTypesById = new Dictionary<Guid, TicketType>();
        if (ticketTypeIds.Count > 0)
        {
            var ticketTypes = await _ticketTypeRepository.GetForUpdateAsync(ticketTypeIds, cancellationToken);
            if (ticketTypes.Count < ticketTypeIds.Count)
            {
                return Result.Failure(Error.NotFound($"One or more ticket types referenced by order '{orderId}' were not found."));
            }

            ticketTypesById = ticketTypes.ToDictionary(t => t.Id);
        }

        // 鎖後重讀，避免兩個並發的同類操作（尤其是兩個並發 Cancel）其中一個誤報成功
        // （見 ticketing-purchase design.md 決策 3，不可省略）。
        await _orderRepository.ReloadAsync(order, cancellationToken);

        var result = await handle(order, eventSeatsById, ticketTypesById, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
