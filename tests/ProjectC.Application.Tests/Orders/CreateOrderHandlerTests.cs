using FluentAssertions;
using ProjectC.Application.Orders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders;

public class CreateOrderHandlerTests
{
    private static (Event Event, SeatMap SeatMap) CreateEventWithSeatMap()
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        return (@event, seatMap);
    }

    private static SeatSelection CreateSeatSelection(
        Event @event, SeatMap seatMap, string seatNumber, string zoneCode = "A", decimal price = 500m)
    {
        var seat = seatMap.AddSeat(zoneCode, seatNumber);
        var eventSeat = @event.CreateEventSeats(seatMap).Single(s => s.SeatId == seat.Id);
        var ticketType = @event.CreateTicketType(zoneCode, price, seatMap);
        return new SeatSelection(eventSeat, ticketType);
    }

    [Fact]
    public void Handle_WhenAllSeatsAvailable_CreatesPendingOrderAndHoldsAllSeats()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var (@event, seatMap) = CreateEventWithSeatMap();
        var selectionA = CreateSeatSelection(@event, seatMap, "1");
        var selectionB = CreateSeatSelection(@event, seatMap, "2");

        var result = handler.Handle(Guid.NewGuid(), [selectionA, selectionB], []);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(OrderStatus.Pending);
        result.Value.EventId.Should().Be(@event.Id);
        result.Value.Items.Should().HaveCount(2);
        selectionA.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Held);
        selectionB.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Held);
    }

    [Fact]
    public void Handle_WhenOneSeatAlreadyHeld_FailsAndReleasesOnlySeatsHeldDuringThisAttempt()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var (@event, seatMap) = CreateEventWithSeatMap();
        var selectionA = CreateSeatSelection(@event, seatMap, "1");
        var selectionB = CreateSeatSelection(@event, seatMap, "2");
        var otherOrderId = Guid.NewGuid();
        selectionB.EventSeat.Hold(otherOrderId, now.AddMinutes(10), now);

        var result = handler.Handle(Guid.NewGuid(), [selectionA, selectionB], []);

        result.IsSuccess.Should().BeFalse();
        selectionA.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Available);
        selectionB.EventSeat.IsHeldBy(otherOrderId, now).Should().BeTrue();
    }

    [Fact]
    public void Handle_RecordsGivenBuyerIdOnCreatedOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var (@event, seatMap) = CreateEventWithSeatMap();
        var selection = CreateSeatSelection(@event, seatMap, "1");
        var buyerId = Guid.NewGuid();

        var result = handler.Handle(buyerId, [selection], []);

        result.Value!.BuyerId.Should().Be(buyerId);
    }

    [Fact]
    public void Handle_SnapshotsTicketTypePriceIntoOrderItemUnitPrice()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var (@event, seatMap) = CreateEventWithSeatMap();
        var selection = CreateSeatSelection(@event, seatMap, "1", price: 750m);

        var result = handler.Handle(Guid.NewGuid(), [selection], []);

        result.Value!.Items.Single().UnitPrice.Should().Be(750m);
    }

    [Fact]
    public void Handle_AppliesSameExpiryToAllSeatsInOrder()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var (@event, seatMap) = CreateEventWithSeatMap();
        var selectionA = CreateSeatSelection(@event, seatMap, "1");
        var selectionB = CreateSeatSelection(@event, seatMap, "2");

        var result = handler.Handle(Guid.NewGuid(), [selectionA, selectionB], []);

        var afterExpiry = result.Value!.HeldUntilUtc.AddMinutes(1);
        selectionA.EventSeat.GetStatus(afterExpiry).Should().Be(EventSeatStatus.Available);
        selectionB.EventSeat.GetStatus(afterExpiry).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void Handle_WhenNoSeatsSelected_ReturnsFailure()
    {
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = DateTime.UtcNow });

        var result = handler.Handle(Guid.NewGuid(), [], []);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Handle_WhenSameSeatSelectedTwice_ReturnsFailureAndDoesNotHoldAnything()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var (@event, seatMap) = CreateEventWithSeatMap();
        var selection = CreateSeatSelection(@event, seatMap, "1");
        var duplicate = new SeatSelection(selection.EventSeat, selection.TicketType);

        var result = handler.Handle(Guid.NewGuid(), [selection, duplicate], []);

        result.IsSuccess.Should().BeFalse();
        selection.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void Handle_WhenSelectionsSpanDifferentEvents_ReturnsFailureAndDoesNotHoldAnything()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var (eventA, seatMapA) = CreateEventWithSeatMap();
        var (eventB, seatMapB) = CreateEventWithSeatMap();
        var selectionA = CreateSeatSelection(eventA, seatMapA, "1");
        var selectionB = CreateSeatSelection(eventB, seatMapB, "1");

        var result = handler.Handle(Guid.NewGuid(), [selectionA, selectionB], []);

        result.IsSuccess.Should().BeFalse();
        selectionA.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Available);
        selectionB.EventSeat.GetStatus(now).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void Handle_WhenTicketTypeBelongsToDifferentEventThanSeat_ReturnsFailureAndDoesNotHoldAnything()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var handler = new CreateOrderHandler(new FakeDateTimeProvider { UtcNow = now });
        var (eventA, seatMapA) = CreateEventWithSeatMap();
        var (eventB, seatMapB) = CreateEventWithSeatMap();
        var seatFromA = CreateSeatSelection(eventA, seatMapA, "1").EventSeat;
        seatMapB.AddSeat("A", "1");
        var ticketTypeFromB = eventB.CreateTicketType("A", 500m, seatMapB);
        var mismatchedSelection = new SeatSelection(seatFromA, ticketTypeFromB);

        var result = handler.Handle(Guid.NewGuid(), [mismatchedSelection], []);

        result.IsSuccess.Should().BeFalse();
        seatFromA.GetStatus(now).Should().Be(EventSeatStatus.Available);
    }
}
