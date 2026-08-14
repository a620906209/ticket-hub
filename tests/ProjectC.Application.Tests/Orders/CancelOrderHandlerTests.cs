using FluentAssertions;
using ProjectC.Application.Orders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders;

public class CancelOrderHandlerTests
{
    private static (Order Order, Dictionary<Guid, EventSeat> SeatsById, Event Event, SeatMap SeatMap) CreatePendingOrder(DateTime now)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var seat = seatMap.AddSeat("A", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        var eventSeat = @event.CreateEventSeats(seatMap).Single(s => s.SeatId == seat.Id);
        var ticketType = @event.CreateTicketType("A", 500m, seatMap);

        var createHandler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var result = createHandler.Handle([new SeatSelection(eventSeat, ticketType)]);

        return (result.Value!, new Dictionary<Guid, EventSeat> { [eventSeat.Id] = eventSeat }, @event, seatMap);
    }

    [Fact]
    public void Handle_WhenPending_ReleasesHeldSeatsAndCancelsOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById, _, _) = CreatePendingOrder(now);
        var handler = new CancelOrderHandler(new FakeDateTimeProvider { UtcNow = now });

        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        seatsById.Values.Single().GetStatus(now).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void Handle_WhenPendingButExpired_ReleasesOnlySeatsStillHeldByThisOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById, _, _) = CreatePendingOrder(now);
        var seat = seatsById.Values.Single();

        var afterExpiry = order.HeldUntilUtc.AddMinutes(1);
        var otherOrderId = Guid.NewGuid();
        seat.Hold(otherOrderId, afterExpiry.AddMinutes(10), afterExpiry);

        var handler = new CancelOrderHandler(new FakeDateTimeProvider { UtcNow = afterExpiry });
        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        seat.IsHeldBy(otherOrderId, afterExpiry).Should().BeTrue();
    }

    [Fact]
    public void Handle_WhenConfirmed_FailsAndDoesNotReleaseSeats()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById, _, _) = CreatePendingOrder(now);
        var confirmHandler = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        confirmHandler.Handle(order, seatsById);

        var handler = new CancelOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Confirmed);
        seatsById.Values.Single().GetStatus(now).Should().Be(EventSeatStatus.Sold);
    }

    [Fact]
    public void Handle_WhenAlreadyCancelled_ReturnsFailure()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById, _, _) = CreatePendingOrder(now);
        var handler = new CancelOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        handler.Handle(order, seatsById);

        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public void Handle_WhenSeatWasSoldToAnotherOrderAfterExpiry_ReturnsFailureInsteadOfThrowing()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (orderA, seatsById, @event, seatMap) = CreatePendingOrder(now);
        var seat = seatsById.Values.Single();

        var afterExpiry = orderA.HeldUntilUtc.AddMinutes(1);
        var ticketType = @event.CreateTicketType("A", 500m, seatMap);
        var createHandlerB = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = afterExpiry });
        var resultB = createHandlerB.Handle([new SeatSelection(seat, ticketType)]);
        resultB.IsSuccess.Should().BeTrue();

        var confirmHandlerB = new ConfirmOrderHandler(new FakeDateTimeProvider { UtcNow = afterExpiry });
        confirmHandlerB.Handle(resultB.Value!, seatsById).IsSuccess.Should().BeTrue();

        var cancelHandlerA = new CancelOrderHandler(new FakeDateTimeProvider { UtcNow = afterExpiry });
        var act = () => cancelHandlerA.Handle(orderA, seatsById);

        act.Should().NotThrow();
        act().IsSuccess.Should().BeFalse();
        orderA.Status.Should().Be(OrderStatus.Pending);
    }
}
