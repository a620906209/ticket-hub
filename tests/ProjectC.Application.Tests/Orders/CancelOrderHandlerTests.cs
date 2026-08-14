using FluentAssertions;
using ProjectC.Application.Orders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders;

public class CancelOrderHandlerTests
{
    private static (Order order, Dictionary<Guid, EventSeat> seatsById) CreatePendingOrder(DateTime now)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var seat = seatMap.AddSeat("A", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        var eventSeat = @event.CreateEventSeats(seatMap).Single(s => s.SeatId == seat.Id);
        var ticketType = new TicketType(Guid.NewGuid(), @event.Id, "A", 500m, seatMap);

        var createHandler = new CreateOrderHandler(new FakeDateTimeProvider(now));
        var result = createHandler.Handle([new SeatSelection(eventSeat, ticketType)]);

        return (result.Value!, new Dictionary<Guid, EventSeat> { [eventSeat.Id] = eventSeat });
    }

    [Fact]
    public void Handle_WhenPending_ReleasesHeldSeatsAndCancelsOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var handler = new CancelOrderHandler();

        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        seatsById.Values.Single().GetStatus(now).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void Handle_WhenPendingButExpired_ReleasesOnlySeatsStillHeldByThisOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var seat = seatsById.Values.Single();

        var afterExpiry = order.HeldUntilUtc.AddMinutes(1);
        var otherOrderId = Guid.NewGuid();
        seat.Hold(otherOrderId, afterExpiry.AddMinutes(10), afterExpiry);

        var handler = new CancelOrderHandler();
        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        seat.IsHeldBy(otherOrderId, afterExpiry).Should().BeTrue();
    }

    [Fact]
    public void Handle_WhenConfirmed_FailsAndDoesNotReleaseSeats()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var (order, seatsById) = CreatePendingOrder(now);
        var confirmHandler = new ConfirmOrderHandler(new FakeDateTimeProvider(now));
        confirmHandler.Handle(order, seatsById);

        var handler = new CancelOrderHandler();
        var result = handler.Handle(order, seatsById);

        result.IsSuccess.Should().BeFalse();
        order.Status.Should().Be(OrderStatus.Confirmed);
        seatsById.Values.Single().GetStatus(now).Should().Be(EventSeatStatus.Sold);
    }
}
