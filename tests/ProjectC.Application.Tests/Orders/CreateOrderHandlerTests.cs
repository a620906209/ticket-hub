using FluentAssertions;
using ProjectC.Application.Orders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders;

public class CreateOrderHandlerTests
{
    private static SeatSelection CreateSeatSelection(string zoneCode = "A", decimal price = 500m)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var seat = seatMap.AddSeat(zoneCode, "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        var eventSeat = @event.CreateEventSeats(seatMap).Single(s => s.SeatId == seat.Id);
        var ticketType = new TicketType(Guid.NewGuid(), @event.Id, zoneCode, price, seatMap);
        return new SeatSelection(eventSeat, ticketType);
    }

    [Fact]
    public void Handle_WhenAllSeatsAvailable_CreatesPendingOrderAndHoldsAllSeats()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider(now));
        var selectionA = CreateSeatSelection();
        var selectionB = CreateSeatSelection();

        var result = handler.Handle([selectionA, selectionB]);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OrderStatus.Pending);
        result.Value.Items.Should().HaveCount(2);
        selectionA.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Held);
        selectionB.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Held);
    }

    [Fact]
    public void Handle_WhenOneSeatAlreadyHeld_FailsAndReleasesSeatsHeldDuringThisAttempt()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider(now));
        var selectionA = CreateSeatSelection();
        var selectionB = CreateSeatSelection();
        selectionB.EventSeat.Hold(Guid.NewGuid(), now.AddMinutes(10), now);

        var result = handler.Handle([selectionA, selectionB]);

        result.IsSuccess.Should().BeFalse();
        selectionA.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void Handle_SnapshotsTicketTypePriceIntoOrderItemUnitPrice()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider(now));
        var selection = CreateSeatSelection(price: 750m);

        var result = handler.Handle([selection]);

        result.Value!.Items.Single().UnitPrice.Should().Be(750m);
    }

    [Fact]
    public void Handle_AppliesSameExpiryToAllSeatsInOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider(now));
        var selectionA = CreateSeatSelection();
        var selectionB = CreateSeatSelection();

        var result = handler.Handle([selectionA, selectionB]);

        var afterExpiry = result.Value!.HeldUntilUtc.AddMinutes(1);
        selectionA.EventSeat.GetStatus(afterExpiry).Should().Be(EventSeatStatus.Available);
        selectionB.EventSeat.GetStatus(afterExpiry).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void Handle_WhenNoSeatsSelected_ReturnsFailure()
    {
        var handler = new CreateOrderHandler(new FakeDateTimeProvider(DateTime.UtcNow));

        var result = handler.Handle([]);

        result.IsSuccess.Should().BeFalse();
    }
}
