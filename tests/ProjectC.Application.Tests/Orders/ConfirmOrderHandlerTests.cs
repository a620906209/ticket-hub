using FluentAssertions;
using ProjectC.Application.Orders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Payments;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders;

public class ConfirmOrderHandlerTests
{
    private static (Order order, Dictionary<Guid, EventSeat> seatsById) CreatePendingOrder(DateTime now)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var seat = seatMap.AddSeat("A", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        var eventSeat = @event.CreateEventSeats(seatMap).Single(s => s.SeatId == seat.Id);
        var ticketType = @event.CreateTicketType("A", 500m, seatMap);

        var createHandler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var result = createHandler.Handle(Guid.NewGuid(), [new SeatSelection(eventSeat, ticketType)], []);

        return (result.Value!, new Dictionary<Guid, EventSeat> { [eventSeat.Id] = eventSeat });
    }

    [Fact]
    public async Task Handle_WhenPendingAndSeatsStillHeld_ConfirmsOrderAndMarksSeatsSold()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var paymentGateway = new FakePaymentGateway(PaymentResult.Succeeded);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, paymentGateway, new FakeTicketRepository());

        var result = await handler.Handle(order, seatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
        seatsById.Values.Single().GetStatus(now).Should().Be(EventSeatStatus.Sold);
    }

    [Fact]
    public async Task Handle_WhenPaymentSucceeds_ChargesGatewayWithOrderIdAndTotalAmount()
    {
        // 兩個座位分屬不同分區、不同票價，確保 ChargeAsync 收到的金額是「加總」而不是誤取單一項目
        // （例如不小心寫成 order.Items.First().UnitPrice 這種只取第一筆的錯誤，用單一座位訂單測不出來）。
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var seatA = seatMap.AddSeat("A", "1");
        var seatB = seatMap.AddSeat("B", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        var eventSeats = @event.CreateEventSeats(seatMap).ToList();
        var eventSeatA = eventSeats.Single(s => s.SeatId == seatA.Id);
        var eventSeatB = eventSeats.Single(s => s.SeatId == seatB.Id);
        var ticketTypeA = @event.CreateTicketType("A", 500m, seatMap);
        var ticketTypeB = @event.CreateTicketType("B", 300m, seatMap);

        var createHandler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var createResult = createHandler.Handle(Guid.NewGuid(),
            [new SeatSelection(eventSeatA, ticketTypeA), new SeatSelection(eventSeatB, ticketTypeB)], []);
        var order = createResult.Value!;
        var seatsById = new Dictionary<Guid, EventSeat> { [eventSeatA.Id] = eventSeatA, [eventSeatB.Id] = eventSeatB };

        var paymentGateway = new FakePaymentGateway(PaymentResult.Succeeded);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, paymentGateway, new FakeTicketRepository());

        await handler.Handle(order, seatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        paymentGateway.CallCount.Should().Be(1);
        paymentGateway.LastOrderId.Should().Be(order.Id);
        paymentGateway.LastAmount.Should().Be(800m);
        paymentGateway.LastAmount.Should().Be(order.Items.Sum(i => i.UnitPrice * i.Quantity));
    }

    [Fact]
    public async Task Handle_WhenPaymentDeclined_FailsAndDoesNotMarkSeatsSoldOrIssueTickets()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var paymentGateway = new FakePaymentGateway(PaymentResult.Declined);
        var ticketRepository = new FakeTicketRepository();
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, paymentGateway, ticketRepository);

        var result = await handler.Handle(order, seatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
        seatsById.Values.Single().GetStatus(now).Should().Be(EventSeatStatus.Held);
        ticketRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenOrderHasThreeSeatItems_IssuesThreeTicketsEachLinkedToItsOwnOrderItem()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var seats = new[] { seatMap.AddSeat("A", "1"), seatMap.AddSeat("A", "2"), seatMap.AddSeat("A", "3") };
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        var eventSeats = @event.CreateEventSeats(seatMap).ToList();
        var ticketType = @event.CreateTicketType("A", 500m, seatMap);
        var selections = seats.Select(s => new SeatSelection(eventSeats.Single(es => es.SeatId == s.Id), ticketType)).ToList();

        var createHandler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var order = createHandler.Handle(Guid.NewGuid(), selections, []).Value!;
        var seatsById = selections.ToDictionary(s => s.EventSeat.Id, s => s.EventSeat);

        var ticketRepository = new FakeTicketRepository();
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, new FakePaymentGateway(PaymentResult.Succeeded), ticketRepository);

        var result = await handler.Handle(order, seatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ticketRepository.Data.Should().HaveCount(3);
        ticketRepository.Data.Should().OnlyContain(t => t.Status == TicketStatus.Issued);
        var orderItemIds = order.Items.Select(i => i.Id).ToHashSet();
        ticketRepository.Data.Select(t => t.OrderItemId).ToHashSet().Should().BeEquivalentTo(orderItemIds);
        ticketRepository.Data.GroupBy(t => t.OrderItemId).Should().OnlyContain(g => g.Count() == 1);
    }

    [Fact]
    public async Task Handle_WhenOrderHasCountItemWithQuantityFive_IssuesFiveTicketsAllLinkedToThatOrderItem()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid());
        var ticketType = @event.CreateCountBasedTicketType("GA", 300m, 100);

        var createHandler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var order = createHandler.Handle(Guid.NewGuid(), [], [new QuantitySelection(ticketType, 5)]).Value!;
        var ticketTypesById = new Dictionary<Guid, TicketType> { [ticketType.Id] = ticketType };

        var ticketRepository = new FakeTicketRepository();
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, new FakePaymentGateway(PaymentResult.Succeeded), ticketRepository);

        var result = await handler.Handle(order, new Dictionary<Guid, EventSeat>(), ticketTypesById, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ticketRepository.Data.Should().HaveCount(5);
        ticketRepository.Data.Should().OnlyContain(t => t.Status == TicketStatus.Issued);
        var orderItemId = order.Items.Single().Id;
        ticketRepository.Data.Should().OnlyContain(t => t.OrderItemId == orderItemId);
    }

    [Fact]
    public async Task Handle_WhenOrderHasMixedSeatAndCountItems_IssuesTicketsSummingCorrectlyAndLinkedToCorrectOrderItems()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var seatA = seatMap.AddSeat("A", "1");
        var seatB = seatMap.AddSeat("A", "2");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        var eventSeats = @event.CreateEventSeats(seatMap).ToList();
        var eventSeatA = eventSeats.Single(s => s.SeatId == seatA.Id);
        var eventSeatB = eventSeats.Single(s => s.SeatId == seatB.Id);
        var seatTicketType = @event.CreateTicketType("A", 500m, seatMap);
        var countTicketType = @event.CreateCountBasedTicketType("GA", 300m, 100);

        var seatSelections = new[] { new SeatSelection(eventSeatA, seatTicketType), new SeatSelection(eventSeatB, seatTicketType) };
        var quantitySelections = new[] { new QuantitySelection(countTicketType, 3) };

        var createHandler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var order = createHandler.Handle(Guid.NewGuid(), seatSelections, quantitySelections).Value!;
        var seatsById = new Dictionary<Guid, EventSeat> { [eventSeatA.Id] = eventSeatA, [eventSeatB.Id] = eventSeatB };
        var ticketTypesById = new Dictionary<Guid, TicketType> { [countTicketType.Id] = countTicketType };

        var ticketRepository = new FakeTicketRepository();
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, new FakePaymentGateway(PaymentResult.Succeeded), ticketRepository);

        var result = await handler.Handle(order, seatsById, ticketTypesById, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ticketRepository.Data.Should().HaveCount(5);

        var seatOrderItemIds = order.Items.Where(i => i.EventSeatId.HasValue).Select(i => i.Id).ToList();
        var countOrderItemId = order.Items.Single(i => !i.EventSeatId.HasValue).Id;

        ticketRepository.Data.Count(t => seatOrderItemIds.Contains(t.OrderItemId)).Should().Be(2);
        ticketRepository.Data.Where(t => seatOrderItemIds.Contains(t.OrderItemId))
            .GroupBy(t => t.OrderItemId).Should().OnlyContain(g => g.Count() == 1);
        ticketRepository.Data.Count(t => t.OrderItemId == countOrderItemId).Should().Be(3);
    }

    [Fact]
    public async Task Handle_WhenOrderExpired_FailsAndDoesNotMarkSeatsSold()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var afterExpiry = order.HeldUntilUtc.AddMinutes(1);
        var paymentGateway = new FakePaymentGateway(PaymentResult.Succeeded);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = afterExpiry }, paymentGateway, new FakeTicketRepository());

        var result = await handler.Handle(order, seatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
        seatsById.Values.Single().GetStatus(afterExpiry).Should().Be(EventSeatStatus.Available);
        paymentGateway.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenSeatNoLongerHeldByThisOrder_FailsAndDoesNotChangeAnything()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var seat = seatsById.Values.Single();
        seat.ReleaseHold(order.Id);
        seat.Hold(Guid.NewGuid(), now.AddMinutes(30), now);

        var paymentGateway = new FakePaymentGateway(PaymentResult.Succeeded);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, paymentGateway, new FakeTicketRepository());
        var result = await handler.Handle(order, seatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
        paymentGateway.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenOrderNotPending_ReturnsFailure()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        order.Confirm();

        var paymentGateway = new FakePaymentGateway(PaymentResult.Succeeded);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, paymentGateway, new FakeTicketRepository());
        var result = await handler.Handle(order, seatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        paymentGateway.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenSeatCannotBeResolved_ReturnsFailureAndDoesNotChangeOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, _) = CreatePendingOrder(now);
        var emptySeatsById = new Dictionary<Guid, EventSeat>();

        var paymentGateway = new FakePaymentGateway(PaymentResult.Succeeded);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, paymentGateway, new FakeTicketRepository());
        var result = await handler.Handle(order, emptySeatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
        paymentGateway.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenResolvedSeatBelongsToDifferentEvent_ReturnsFailureAndDoesNotChangeAnything()
    {
        // 模擬呼叫端組出來的 seatsById 字典裡，某個 EventSeatId 對應到錯誤活動的座位物件
        // （例如 Repository 查詢寫錯條件），不是 CreateOrderHandler 正常流程會產生的狀態。
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var seatId = seatsById.Keys.Single();

        var otherSeatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var otherSeat = otherSeatMap.AddSeat("A", "1");
        var otherEvent = new Event(Guid.NewGuid(), "Other Show", DateTime.UtcNow.AddDays(2), Guid.NewGuid(), otherSeatMap.Id);
        var otherEventSeat = otherEvent.CreateEventSeats(otherSeatMap).Single(s => s.SeatId == otherSeat.Id);
        var mismatchedSeatsById = new Dictionary<Guid, EventSeat> { [seatId] = otherEventSeat };

        var paymentGateway = new FakePaymentGateway(PaymentResult.Succeeded);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now }, paymentGateway, new FakeTicketRepository());
        var result = await handler.Handle(order, mismatchedSeatsById, new Dictionary<Guid, TicketType>(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
        otherEventSeat.GetStatus(now).Should().Be(EventSeatStatus.Available);
        paymentGateway.CallCount.Should().Be(0);
    }
}
