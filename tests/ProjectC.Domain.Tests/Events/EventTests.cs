using FluentAssertions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tests.Events;

public class EventTests
{
    private static SeatMap CreateSeatMap(int seatCount)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        for (var i = 1; i <= seatCount; i++)
            seatMap.AddSeat("A", i.ToString());

        return seatMap;
    }

    [Fact]
    public void Constructor_WhenAllRequiredFieldsProvided_CreatesEvent()
    {
        var seatMap = CreateSeatMap(1);

        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMap.Id);

        @event.Title.Should().Be("Concert");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WhenTitleIsMissing_ThrowsArgumentException(string title)
    {
        var act = () => new Event(Guid.NewGuid(), title, DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenStartTimeIsMissing_ThrowsArgumentException()
    {
        var act = () => new Event(Guid.NewGuid(), "Concert", default, Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenMaxTicketsPerOrderIsNotPositive_ThrowsArgumentException(int maxTicketsPerOrder)
    {
        var act = () => new Event(
            Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(),
            maxTicketsPerOrder: maxTicketsPerOrder);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenMaxTicketsPerOrderIsNull_AllowsUnlimitedTicketsPerOrder()
    {
        var @event = new Event(
            Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(),
            maxTicketsPerOrder: null);

        @event.MaxTicketsPerOrder.Should().BeNull();
    }

    [Fact]
    public void CreateEventSeats_WhenSeatMapHasNSeats_CreatesNAvailableEventSeats()
    {
        var seatMap = CreateSeatMap(3);
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMap.Id);

        var eventSeats = @event.CreateEventSeats(seatMap);

        eventSeats.Should().HaveCount(3);
        eventSeats.Should().OnlyContain(seat => seat.GetStatus(DateTime.UtcNow) == EventSeatStatus.Available);
        eventSeats.Select(s => s.SeatId).Should().BeEquivalentTo(seatMap.Seats.Select(s => s.Id));
    }

    [Fact]
    public void CreateEventSeats_ForTwoEventsSharingSameSeatMap_ProducesIndependentInventory()
    {
        var seatMap = CreateSeatMap(1);
        var eventA = new Event(Guid.NewGuid(), "Show A", DateTime.UtcNow.AddDays(10), Guid.NewGuid(), seatMap.Id);
        var eventB = new Event(Guid.NewGuid(), "Show B", DateTime.UtcNow.AddDays(20), Guid.NewGuid(), seatMap.Id);

        var seatsA = eventA.CreateEventSeats(seatMap);
        var seatsB = eventB.CreateEventSeats(seatMap);

        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        seatsA[0].Hold(orderId, now.AddMinutes(10), now);
        seatsA[0].ConfirmSold(orderId, now);

        seatsA[0].GetStatus(now).Should().Be(EventSeatStatus.Sold);
        seatsB[0].GetStatus(now).Should().Be(EventSeatStatus.Available);
        seatsA[0].Id.Should().NotBe(seatsB[0].Id);
    }

    [Fact]
    public void CreateEventSeats_EachSeatTemplateMapsToExactlyOneEventSeat()
    {
        var seatMap = CreateSeatMap(5);
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMap.Id);

        var eventSeats = @event.CreateEventSeats(seatMap);

        eventSeats.Select(s => s.SeatId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CreateEventSeats_WhenSeatMapDoesNotBelongToEvent_ThrowsArgumentException()
    {
        var seatMap = CreateSeatMap(1);
        var otherSeatMap = CreateSeatMap(1);
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMap.Id);

        var act = () => @event.CreateEventSeats(otherSeatMap);

        act.Should().Throw<ArgumentException>();
    }
}
