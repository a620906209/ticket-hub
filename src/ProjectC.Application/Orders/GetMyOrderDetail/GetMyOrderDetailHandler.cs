using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Orders.GetMyOrderDetail;

public sealed class GetMyOrderDetailHandler
{
    private readonly IOrderRepository _orderRepository;
    private readonly ITicketRepository _ticketRepository;
    private readonly IEventRepository _eventRepository;
    private readonly ITicketTypeRepository _ticketTypeRepository;
    private readonly IEventSeatRepository _eventSeatRepository;
    private readonly ISeatMapRepository _seatMapRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetMyOrderDetailHandler(
        IOrderRepository orderRepository,
        ITicketRepository ticketRepository,
        IEventRepository eventRepository,
        ITicketTypeRepository ticketTypeRepository,
        IEventSeatRepository eventSeatRepository,
        ISeatMapRepository seatMapRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _orderRepository = orderRepository;
        _ticketRepository = ticketRepository;
        _eventRepository = eventRepository;
        _ticketTypeRepository = ticketTypeRepository;
        _eventSeatRepository = eventSeatRepository;
        _seatMapRepository = seatMapRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<MyOrderDetailDto>> HandleAsync(Guid orderId, Guid callerBuyerId, CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Result<MyOrderDetailDto>.Failure(Error.NotFound($"Order '{orderId}' was not found."));
        }

        if (order.BuyerId != callerBuyerId)
        {
            return Result<MyOrderDetailDto>.Failure(Error.Forbidden("You are not the buyer of this order."));
        }

        var orderItemIds = order.Items.Select(item => item.Id).ToList();
        var ticketsByOrderItemId = (await _ticketRepository.GetByOrderItemIdsAsync(orderItemIds, cancellationToken))
            .GroupBy(ticket => ticket.OrderItemId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MyTicketDto>)group
                .Select(ticket => new MyTicketDto(ticket.Id, ticket.Status.ToString()))
                .ToList());

        // 以下關聯皆有 FK 且系統沒有刪除端點，查不到代表資料損毀：一律大聲失敗交由全域例外處理轉 500，
        // 訊息帶訂單 Id 與查不到的關聯 Id 供維運定位（只進 log，不進 ProblemDetails）——order-display-enrichment design.md 決策 2。
        var @event = await _eventRepository.GetByIdAsync(order.EventId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Order '{order.Id}' references Event '{order.EventId}' that does not exist.");
        var ticketTypeNamesById = await GetTicketTypeNamesByIdAsync(order, cancellationToken);
        var seatsByEventSeatId = await GetSeatsByEventSeatIdAsync(order, cancellationToken);

        var items = order.Items
            .Select(item =>
            {
                var seat = item.EventSeatId is { } eventSeatId ? seatsByEventSeatId[eventSeatId] : null;
                return new MyOrderItemDto(
                    item.Id,
                    item.EventSeatId,
                    item.TicketTypeId,
                    seat?.ZoneCode,
                    seat?.SeatNumber,
                    item.TicketTypeId is { } ticketTypeId ? ticketTypeNamesById[ticketTypeId] : null,
                    item.Quantity,
                    item.UnitPrice,
                    ticketsByOrderItemId.GetValueOrDefault(item.Id, []));
            })
            .ToList();
        var dto = new MyOrderDetailDto(
            order.Id,
            order.EventId,
            @event.Title,
            order.GetStatus(_dateTimeProvider.UtcNow).ToString(),
            order.HeldUntilUtc,
            items);

        return Result<MyOrderDetailDto>.Success(dto);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> GetTicketTypeNamesByIdAsync(Order order, CancellationToken cancellationToken)
    {
        // 系統沒有獨立的票種名稱欄位，以 ZoneCode 作為票種顯示名稱（design.md 決策 3）。
        var ticketTypeNamesById = (await _ticketTypeRepository.GetByEventIdAsync(order.EventId, cancellationToken))
            .ToDictionary(ticketType => ticketType.Id, ticketType => ticketType.ZoneCode);

        var missingTicketTypeId = order.Items
            .Select(item => item.TicketTypeId)
            .Where(ticketTypeId => ticketTypeId is not null)
            .Distinct()
            .FirstOrDefault(ticketTypeId => !ticketTypeNamesById.ContainsKey(ticketTypeId!.Value));
        if (missingTicketTypeId is not null)
        {
            throw new InvalidOperationException(
                $"Order '{order.Id}' references TicketType '{missingTicketTypeId}' that does not exist.");
        }

        return ticketTypeNamesById;
    }

    /// <summary>只在訂單含座位項目時查詢，且只讀取用到的座位範本，不載入整張座位圖（design.md 決策 1）。</summary>
    private async Task<IReadOnlyDictionary<Guid, Seat>> GetSeatsByEventSeatIdAsync(Order order, CancellationToken cancellationToken)
    {
        var eventSeatIds = order.Items
            .Where(item => item.EventSeatId is not null)
            .Select(item => item.EventSeatId!.Value)
            .Distinct()
            .ToList();
        if (eventSeatIds.Count == 0)
        {
            return new Dictionary<Guid, Seat>();
        }

        var eventSeatsById = (await _eventSeatRepository.GetByIdsAsync(eventSeatIds, cancellationToken))
            .ToDictionary(eventSeat => eventSeat.Id);
        var missingEventSeatId = eventSeatIds.Cast<Guid?>().FirstOrDefault(eventSeatId => !eventSeatsById.ContainsKey(eventSeatId!.Value));
        if (missingEventSeatId is not null)
        {
            throw new InvalidOperationException(
                $"Order '{order.Id}' references EventSeat '{missingEventSeatId}' that does not exist.");
        }

        var seatIds = eventSeatsById.Values.Select(eventSeat => eventSeat.SeatId).Distinct().ToList();
        var seatsById = (await _seatMapRepository.GetSeatsByIdsAsync(seatIds, cancellationToken))
            .ToDictionary(seat => seat.Id);
        var eventSeatWithMissingSeat = eventSeatsById.Values.FirstOrDefault(eventSeat => !seatsById.ContainsKey(eventSeat.SeatId));
        if (eventSeatWithMissingSeat is not null)
        {
            throw new InvalidOperationException(
                $"Order '{order.Id}' references EventSeat '{eventSeatWithMissingSeat.Id}' whose Seat '{eventSeatWithMissingSeat.SeatId}' does not exist.");
        }

        return eventSeatsById.ToDictionary(pair => pair.Key, pair => seatsById[pair.Value.SeatId]);
    }
}
