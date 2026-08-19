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

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        var eventSeatIds = request.Selections.Select(s => s.EventSeatId).ToList();
        var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
        if (eventSeats.Count < eventSeatIds.Count)
        {
            return Result<Guid>.Failure(Error.NotFound("One or more selected seats were not found."));
        }

        var eventSeatsById = eventSeats.ToDictionary(es => es.Id);

        // 每筆訂單限購張數：只用第一個座位所屬的活動去查限制，若選位橫跨多場活動（本來就是不合法的
        // 輸入），交給下面 CreateOrderHandler.Handle 的「所有座位須屬於同一場活動」檢查去擋，這裡的
        // 提早檢查頂多用錯活動的限制值、不會誤放行不合法的輸入。
        var firstEventId = eventSeats[0].EventId;
        var orderEvent = await _eventRepository.GetByIdAsync(firstEventId, cancellationToken);
        if (orderEvent is { MaxTicketsPerOrder: { } maxTicketsPerOrder } && request.Selections.Count > maxTicketsPerOrder)
        {
            return Result<Guid>.Failure(Error.Validation($"This event allows at most {maxTicketsPerOrder} ticket(s) per order."));
        }

        // 分區比對：座位實際所屬分區 MUST 與所選票種的分區一致，防止用低價分區的票種配高價分區的座位
        // （見 ticketing-purchase design.md 決策 2 第 4 點）。座位圖依 EventId 快取，正常情況下所有
        // 項目屬於同一場活動只查一次；跨活動的狀況交給下面 CreateOrderHandler.Handle 再擋一次。
        var seatMapsByEventId = new Dictionary<Guid, SeatMap>();
        var selections = new List<SeatSelection>(request.Selections.Count);

        foreach (var selectionRequest in request.Selections)
        {
            var eventSeat = eventSeatsById[selectionRequest.EventSeatId];
            var ticketType = ticketTypesById[selectionRequest.TicketTypeId];

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

            selections.Add(new SeatSelection(eventSeat, ticketType));
        }

        var result = _createOrderHandler.Handle(buyerId, selections);
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
    private static Func<Order, IReadOnlyDictionary<Guid, EventSeat>, CancellationToken, Task<Result>> WrapSync(
        Func<Order, IReadOnlyDictionary<Guid, EventSeat>, Result> syncHandle)
        => (order, seats, _) => Task.FromResult(syncHandle(order, seats));

    private async Task<Result> ChangeOrderStatusAsync(
        Guid orderId,
        Guid? requestingBuyerId,
        Func<Order, IReadOnlyDictionary<Guid, EventSeat>, CancellationToken, Task<Result>> handle,
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

        var eventSeatIds = order.Items.Select(i => i.EventSeatId).Distinct().ToList();
        var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
        if (eventSeats.Count < eventSeatIds.Count)
        {
            // 理論上不應該發生：目前系統沒有刪除 EventSeat 的路徑，order.Items 引用的座位建立後就一直存在。
            // 用 NotFound 而非 Conflict，跟 ConfirmOrderHandler 自己對「查不到座位」的既有分類一致
            // （見 src/ProjectC.Application/Orders/ConfirmOrderHandler.cs），維持 Confirm/Cancel 行為對稱。
            return Result.Failure(Error.NotFound($"One or more seats referenced by order '{orderId}' were not found."));
        }

        // 鎖後重讀，避免兩個並發的同類操作（尤其是兩個並發 Cancel）其中一個誤報成功
        // （見 ticketing-purchase design.md 決策 3，不可省略）。
        await _orderRepository.ReloadAsync(order, cancellationToken);

        var eventSeatsById = eventSeats.ToDictionary(es => es.Id);
        var result = await handle(order, eventSeatsById, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
