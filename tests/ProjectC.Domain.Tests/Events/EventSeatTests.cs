using FluentAssertions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tests.Events;

public class EventSeatTests
{
    private static EventSeat CreateEventSeat()
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        var seat = seatMap.AddSeat("A", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        return @event.CreateEventSeats(seatMap).Single(s => s.SeatId == seat.Id);
    }

    [Fact]
    public void Hold_WhenAvailable_TransitionsToHeld()
    {
        var eventSeat = CreateEventSeat();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        eventSeat.Hold(orderId, now.AddMinutes(10), now);

        eventSeat.GetStatus(now).Should().Be(EventSeatStatus.Held);
        eventSeat.IsHeldBy(orderId, now).Should().BeTrue();
    }

    [Fact]
    public void Hold_WhenAlreadyHeldAndNotExpired_ThrowsSeatAlreadyHeldException()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        eventSeat.Hold(Guid.NewGuid(), now.AddMinutes(10), now);

        var act = () => eventSeat.Hold(Guid.NewGuid(), now.AddMinutes(10), now);

        act.Should().Throw<SeatAlreadyHeldException>();
    }

    [Fact]
    public void Hold_WhenSold_ThrowsSeatAlreadySoldException()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        eventSeat.Hold(orderId, now.AddMinutes(10), now);
        eventSeat.ConfirmSold(orderId, now);

        var act = () => eventSeat.Hold(Guid.NewGuid(), now.AddMinutes(10), now);

        act.Should().Throw<SeatAlreadySoldException>();
    }

    [Fact]
    public void GetStatus_WhenSold_IgnoresElapsedTime()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        eventSeat.Hold(orderId, now.AddMinutes(10), now);
        eventSeat.ConfirmSold(orderId, now);

        eventSeat.GetStatus(now.AddYears(1)).Should().Be(EventSeatStatus.Sold);
    }

    [Fact]
    public void GetStatus_WhenNowEqualsHeldUntilUtc_ReturnsAvailable()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var heldUntilUtc = now.AddMinutes(10);
        eventSeat.Hold(Guid.NewGuid(), heldUntilUtc, now);

        eventSeat.GetStatus(heldUntilUtc).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void IsAvailableForHold_MatchesGetStatusAvailable()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;

        eventSeat.IsAvailableForHold(now).Should().Be(eventSeat.GetStatus(now) == EventSeatStatus.Available);
    }

    [Fact]
    public void Hold_WhenExistingHoldHasExpired_OverwritesWithNewOrder()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var originalOrderId = Guid.NewGuid();
        eventSeat.Hold(originalOrderId, now.AddMinutes(10), now);

        var afterExpiry = now.AddMinutes(11);
        var newOrderId = Guid.NewGuid();
        eventSeat.Hold(newOrderId, afterExpiry.AddMinutes(10), afterExpiry);

        eventSeat.GetStatus(afterExpiry).Should().Be(EventSeatStatus.Held);
        eventSeat.IsHeldBy(newOrderId, afterExpiry).Should().BeTrue();
        eventSeat.IsHeldBy(originalOrderId, afterExpiry).Should().BeFalse();
    }

    [Fact]
    public void ConfirmSold_WhenHeldByCallingOrderAndNotExpired_TransitionsToSold()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        eventSeat.Hold(orderId, now.AddMinutes(10), now);

        eventSeat.ConfirmSold(orderId, now);

        eventSeat.GetStatus(now).Should().Be(EventSeatStatus.Sold);
    }

    [Fact]
    public void ConfirmSold_WhenCalledByDifferentOrder_ThrowsSeatNotHeldException()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        eventSeat.Hold(Guid.NewGuid(), now.AddMinutes(10), now);

        var act = () => eventSeat.ConfirmSold(Guid.NewGuid(), now);

        act.Should().Throw<SeatNotHeldException>();
        eventSeat.GetStatus(now).Should().Be(EventSeatStatus.Held);
    }

    [Fact]
    public void ConfirmSold_WhenHoldHasExpired_ThrowsSeatNotHeldException()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        eventSeat.Hold(orderId, now.AddMinutes(10), now);

        var afterExpiry = now.AddMinutes(11);
        var act = () => eventSeat.ConfirmSold(orderId, afterExpiry);

        act.Should().Throw<SeatNotHeldException>();
    }

    [Fact]
    public void ReleaseHold_WhenCalledByHoldingOrder_TransitionsToAvailable()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        eventSeat.Hold(orderId, now.AddMinutes(10), now);

        eventSeat.ReleaseHold(orderId);

        eventSeat.GetStatus(now).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public void ReleaseHold_WhenSold_ThrowsSeatAlreadySoldException()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        eventSeat.Hold(orderId, now.AddMinutes(10), now);
        eventSeat.ConfirmSold(orderId, now);

        var act = () => eventSeat.ReleaseHold(orderId);

        act.Should().Throw<SeatAlreadySoldException>();
    }

    [Fact]
    public void ReleaseHold_WhenCalledByNonHoldingOrder_IsNoOpAndDoesNotThrow()
    {
        var eventSeat = CreateEventSeat();
        var now = DateTime.UtcNow;
        var holderOrderId = Guid.NewGuid();
        eventSeat.Hold(holderOrderId, now.AddMinutes(10), now);

        var act = () => eventSeat.ReleaseHold(Guid.NewGuid());

        act.Should().NotThrow();
        eventSeat.IsHeldBy(holderOrderId, now).Should().BeTrue();
    }

    [Fact]
    public void IsHeldBy_WhenSoldHeldByDifferentOrderOrExpired_ReturnsFalse()
    {
        var now = DateTime.UtcNow;

        var soldSeat = CreateEventSeat();
        var soldOrderId = Guid.NewGuid();
        soldSeat.Hold(soldOrderId, now.AddMinutes(10), now);
        soldSeat.ConfirmSold(soldOrderId, now);
        soldSeat.IsHeldBy(soldOrderId, now).Should().BeFalse();

        var heldByOther = CreateEventSeat();
        heldByOther.Hold(Guid.NewGuid(), now.AddMinutes(10), now);
        heldByOther.IsHeldBy(Guid.NewGuid(), now).Should().BeFalse();

        var expiredHold = CreateEventSeat();
        var expiredOrderId = Guid.NewGuid();
        expiredHold.Hold(expiredOrderId, now.AddMinutes(10), now);
        expiredHold.IsHeldBy(expiredOrderId, now.AddMinutes(11)).Should().BeFalse();
    }
}
