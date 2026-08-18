using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders;

public class OrderServiceTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public FakeEventRepository EventRepository { get; } = new();
        public FakeEventSeatRepository EventSeatRepository { get; } = new();
        public FakeSeatMapRepository SeatMapRepository { get; } = new();
        public FakeTicketTypeRepository TicketTypeRepository { get; } = new();
        public FakeOrderRepository OrderRepository { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeDateTimeProvider DateTimeProvider { get; } = new() { UtcNow = Now };

        public OrderService CreateOrderService() => new(
            TicketTypeRepository,
            EventSeatRepository,
            EventRepository,
            SeatMapRepository,
            OrderRepository,
            UnitOfWork,
            new PlaceOrderRequestValidator(),
            DateTimeProvider,
            new CreateOrderHandler(DateTimeProvider),
            new ConfirmOrderHandler(DateTimeProvider),
            new CancelOrderHandler(DateTimeProvider));

        public (Event Event, SeatMap SeatMap, EventSeat EventSeat, TicketType TicketType) SeedEventWithSeatAndTicketType(
            string seatZoneCode = "A", string ticketTypeZoneCode = "A")
        {
            var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            var seat = seatMap.AddSeat(seatZoneCode, "1");
            var @event = new Event(Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), seatMap.Id);
            var eventSeat = @event.CreateEventSeats(seatMap).Single(s => s.SeatId == seat.Id);

            if (ticketTypeZoneCode != seatZoneCode)
                seatMap.AddSeat(ticketTypeZoneCode, "2");
            var ticketType = @event.CreateTicketType(ticketTypeZoneCode, 500m, seatMap);

            EventRepository.Data.Add(@event);
            SeatMapRepository.Data.Add(seatMap);
            EventSeatRepository.Data.Add(eventSeat);
            TicketTypeRepository.Data.Add(ticketType);

            return (@event, seatMap, eventSeat, ticketType);
        }

        public (Event Event, TicketType TicketType, List<EventSeat> EventSeats) SeedEventWithMultipleSeats(
            int seatCount, int? maxTicketsPerOrder, string zoneCode = "A")
        {
            var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            var seatTemplates = Enumerable.Range(1, seatCount).Select(n => seatMap.AddSeat(zoneCode, n.ToString())).ToList();
            var @event = new Event(
                Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), seatMap.Id, maxTicketsPerOrder: maxTicketsPerOrder);
            var eventSeats = @event.CreateEventSeats(seatMap).ToList();
            var ticketType = @event.CreateTicketType(zoneCode, 500m, seatMap);

            EventRepository.Data.Add(@event);
            SeatMapRepository.Data.Add(seatMap);
            EventSeatRepository.Data.AddRange(eventSeats);
            TicketTypeRepository.Data.Add(ticketType);

            return (@event, ticketType, eventSeats);
        }
    }

    // ---- PlaceOrderAsync ----

    [Fact]
    public async Task PlaceOrderAsync_WithValidSeatAndMatchingZoneTicketType_CreatesOrderAndCommits()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var buyerId = Guid.NewGuid();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.OrderRepository.Data.Should().ContainSingle(o => o.Id == result.Value && o.BuyerId == buyerId);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenTicketTypeDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, _) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, Guid.NewGuid())]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();
        var (_, _, _, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(Guid.NewGuid(), ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatZoneDoesNotMatchTicketTypeZone_ReturnsValidationError()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(seatZoneCode: "A", ticketTypeZoneCode: "B");
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSelectionsExceedEventMaxTicketsPerOrder_ReturnsValidationErrorAndDoesNotCreateOrder()
    {
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(seatCount: 3, maxTicketsPerOrder: 2);
        var request = new PlaceOrderRequest(eventSeats
            .Select(seat => new PlaceOrderSelectionRequest(seat.Id, ticketType.Id))
            .ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSelectionsAtEventMaxTicketsPerOrder_Succeeds()
    {
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(seatCount: 3, maxTicketsPerOrder: 2);
        var request = new PlaceOrderRequest(eventSeats
            .Take(2)
            .Select(seat => new PlaceOrderSelectionRequest(seat.Id, ticketType.Id))
            .ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.OrderRepository.Data.Should().ContainSingle(o => o.Id == result.Value);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenEventHasNoMaxTicketsPerOrder_AllowsAnySelectionCount()
    {
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(seatCount: 3, maxTicketsPerOrder: null);
        var request = new PlaceOrderRequest(eventSeats
            .Select(seat => new PlaceOrderSelectionRequest(seat.Id, ticketType.Id))
            .ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ---- ConfirmOrderAsync / CancelOrderAsync ----

    private async Task<(Fixture Fixture, Order Order, Guid BuyerId)> PlaceOrderAsync(Fixture fixture)
    {
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var buyerId = Guid.NewGuid();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);
        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);
        return (fixture, fixture.OrderRepository.Data.Single(o => o.Id == result.Value), buyerId);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenBuyerConfirmsOwnPendingOrder_Succeeds()
    {
        var (fixture, order, buyerId) = await PlaceOrderAsync(new Fixture());

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Confirmed);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenBuyerCancelsOwnPendingOrder_Succeeds()
    {
        var (fixture, order, buyerId) = await PlaceOrderAsync(new Fixture());

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenCallerIsNotTheBuyer_ReturnsForbiddenAndDoesNotChangeOrder()
    {
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenCallerIsNotTheBuyer_ReturnsForbiddenAndDoesNotChangeOrder()
    {
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateOrderService().CancelOrderAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenOrderReferencesASeatThatNoLongerExists_ReturnsNotFound()
    {
        var (fixture, order, buyerId) = await PlaceOrderAsync(new Fixture());
        // 模擬「order.Items 引用的座位查不到」這個理論上不該發生的內部資料不一致情境
        // （見 ticketing-purchase design.md 決策 2 第 4 點）：直接把 Fake 座位資料清空。
        fixture.EventSeatRepository.Data.Clear();

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        order.Status.Should().Be(OrderStatus.Pending);
    }

    // ---- CancelExpiredOrderAsync ----

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenOrderIsExpired_SucceedsWithoutAnyBuyerIdentity()
    {
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());
        fixture.DateTimeProvider.UtcNow = order.HeldUntilUtc.AddSeconds(1);

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenOrderIsNotYetExpired_ReturnsConflictAndDoesNotChangeOrder()
    {
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());
        fixture.DateTimeProvider.UtcNow = order.HeldUntilUtc.AddSeconds(-1);

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenNowEqualsHeldUntilUtc_TreatsAsExpiredAndSucceeds()
    {
        // 邊界案例：跟 Order.GetStatus 的 now >= HeldUntilUtc 判斷邊界一致
        // （見 ticketing-order-management tasks.md 4.1）。
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());
        fixture.DateTimeProvider.UtcNow = order.HeldUntilUtc;

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }
}
