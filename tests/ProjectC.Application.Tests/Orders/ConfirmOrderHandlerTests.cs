using FluentAssertions;
using ProjectC.Application.Orders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
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
        var result = createHandler.Handle(Guid.NewGuid(), [new SeatSelection(eventSeat, ticketType)]);

        return (result.Value!, new Dictionary<Guid, EventSeat> { [eventSeat.Id] = eventSeat });
    }

    [Fact]
    public void Handle_WhenPendingAndSeatsStillHeld_ConfirmsOrderAndMarksSeatsSold()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now });

        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Confirmed);
        seatsById.Values.Single().GetStatus(now).Should().Be(EventSeatStatus.Sold);
    }

    [Fact]
    public void Handle_WhenOrderExpired_FailsAndDoesNotMarkSeatsSold()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var afterExpiry = order.HeldUntilUtc.AddMinutes(1);
        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = afterExpiry });

        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
        seatsById.Values.Single().GetStatus(afterExpiry).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void Handle_WhenSeatNoLongerHeldByThisOrder_FailsAndDoesNotChangeAnything()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var seat = seatsById.Values.Single();
        seat.ReleaseHold(order.Id);
        seat.Hold(Guid.NewGuid(), now.AddMinutes(30), now);

        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void Handle_WhenOrderNotPending_ReturnsFailure()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        order.Confirm();

        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Handle_WhenSeatCannotBeResolved_ReturnsFailureAndDoesNotChangeOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, _) = CreatePendingOrder(now);
        var emptySeatsById = new Dictionary<Guid, EventSeat>();

        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var result = handler.Handle(order, emptySeatsById);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void Handle_WhenResolvedSeatBelongsToDifferentEvent_ReturnsFailureAndDoesNotChangeAnything()
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

        var handler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var result = handler.Handle(order, mismatchedSeatsById);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Pending);
        otherEventSeat.GetStatus(now).Should().Be(EventSeatStatus.Available);
    }
}
